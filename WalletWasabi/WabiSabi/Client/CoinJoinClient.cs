using NBitcoin;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Crypto;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Client.CredentialDependencies;
using WalletWasabi.WabiSabi.Crypto;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.Decomposition;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using WalletWasabi.Wallets;

namespace WalletWasabi.WabiSabi.Client
{
	public class CoinJoinClient
	{
		public CoinJoinClient(
			IWabiSabiApiRequestHandler arenaRequestHandler,
			IEnumerable<SmartCoin> coins,
			Kitchen kitchen,
			KeyManager keymanager,
			RoundStateUpdater roundStatusUpdater)
		{
			ArenaRequestHandler = arenaRequestHandler;
			Kitchen = kitchen;
			Keymanager = keymanager;
			RoundStatusUpdater = roundStatusUpdater;
			SecureRandom = new SecureRandom();
			Coins = coins.ToImmutableArray();
		}

		private IEnumerable<SmartCoin> Coins { get; set; }
		private SecureRandom SecureRandom { get; } = new SecureRandom();
		public IWabiSabiApiRequestHandler ArenaRequestHandler { get; }
		public Kitchen Kitchen { get; }
		public KeyManager Keymanager { get; }
		private RoundStateUpdater RoundStatusUpdater { get; }

		private const int MaxInputsRegistrableByWallet = 7; // how many

		public async Task<bool> StartCoinJoinAsync(CancellationToken cancellationToken)
		{
			var currentRoundState = await RoundStatusUpdater.CreateRoundAwaiter(roundState => roundState.Phase == Phase.InputRegistration, cancellationToken).ConfigureAwait(false);


			// This should be roughly log(#inputs), it could be set slightly
			// higher if more inputs are observed but that involves trusting the
			// coordinator with those values. Therefore, conservatively set this
			// so that a maximum of 5 blame rounds are executed.
			// FIXME should smaller rounds abort earlier?
			var tryLimit = 6;

			for (var tries = 0; tries < tryLimit; tries++)
			{
				if (await StartRoundAsync(currentRoundState, cancellationToken).ConfigureAwait(false))
				{
					return true;
				}
				else
				{
					var blameRoundState = await RoundStatusUpdater.CreateRoundAwaiter(roundState => roundState.BlameOf == currentRoundState.Id, cancellationToken).ConfigureAwait(false);
					currentRoundState = blameRoundState;
				}
			}

			return false;
		}

		/// <summary>Attempt to participate in a specified dround.</summary>
		/// <param name="roundState">Defines the round parameter and state information to use.</param>
		/// <returns>Whether or not the round resulted in a successful transaction.</returns>
		public async Task<bool> StartRoundAsync(RoundState roundState, CancellationToken cancellationToken)
		{
			var constructionState = roundState.Assert<ConstructionState>();

			// Register coins.
			var aliceClients = await RegisterCoinsAsync(CreateAliceClients(roundState), cancellationToken).ConfigureAwait(false);
			if (!aliceClients.Any())
			{
				throw new InvalidOperationException("There is no available alices to participate with.");
			}

			// Calculate outputs values
			var registeredCoins = aliceClients.Select(x => x.Coin);
			var outputValues = DecomposeAmounts(registeredCoins, roundState.FeeRate, roundState.CoinjoinState.Parameters.AllowedOutputAmounts.Min);

			// Get all locked internal keys we have and assert we have enough.
			Keymanager.AssertLockedInternalKeysIndexed(howMany: outputValues.Count());
			var allLockedInternalKeys = Keymanager.GetKeys(x => x.IsInternal && x.KeyState == KeyState.Locked);
			var outputTxOuts = outputValues.Zip(allLockedInternalKeys, (amount, hdPubKey) => new TxOut(amount, hdPubKey.P2wpkhScript));

			DependencyGraph dependencyGraph = DependencyGraph.ResolveCredentialDependencies(registeredCoins, outputTxOuts, roundState.FeeRate, roundState.MaxVsizeAllocationPerAlice);
			DependencyGraphTaskScheduler scheduler = new(dependencyGraph);

			// Confirm coins.
			await scheduler.StartConfirmConnectionsAsync(aliceClients, dependencyGraph, roundState.ConnectionConfirmationTimeout, RoundStatusUpdater, cancellationToken).ConfigureAwait(false);

			// Re-issuances.
			var bobClient = CreateBobClient(roundState);
			await scheduler.StartReissuancesAsync(aliceClients, bobClient, cancellationToken).ConfigureAwait(false);

			// Output registration.
			roundState = await RoundStatusUpdater.CreateRoundAwaiter(roundState.Id, rs => rs.Phase == Phase.OutputRegistration, cancellationToken).ConfigureAwait(false);
			await scheduler.StartOutputRegistrationsAsync(outputTxOuts, bobClient, cancellationToken).ConfigureAwait(false);

			// ReadyToSign.
			await ReadyToSignAsync(aliceClients, cancellationToken).ConfigureAwait(false);

			// Signing.
			roundState = await RoundStatusUpdater.CreateRoundAwaiter(roundState.Id, rs => rs.Phase == Phase.TransactionSigning, cancellationToken).ConfigureAwait(false);
			var signingState = roundState.Assert<SigningState>();
			var unsignedCoinJoin = signingState.CreateUnsignedTransaction();

			// Sanity check.
			if (!SanityCheck(outputTxOuts, unsignedCoinJoin))
			{
				throw new InvalidOperationException($"Round ({roundState.Id}): My output is missing.");
			}

			// Send signature.
			await SignTransactionAsync(aliceClients, unsignedCoinJoin, cancellationToken).ConfigureAwait(false);

			var finalRoundState = await RoundStatusUpdater.CreateRoundAwaiter(s => s.Id == roundState.Id && s.Phase == Phase.Ended, cancellationToken).ConfigureAwait(false);
			return finalRoundState.WasTransactionBroadcast;
		}

		private List<AliceClient> CreateAliceClients(RoundState roundState)
		{
			List<AliceClient> aliceClients = new();
			var coins = SelectCoinsForRound(Coins, roundState).Select(x => x.Coin);
			foreach (var coin in coins)
			{
				var aliceArenaClient = new ArenaClient(
					roundState.CreateAmountCredentialClient(SecureRandom),
					roundState.CreateVsizeCredentialClient(SecureRandom),
					ArenaRequestHandler);

				var hdKey = Keymanager.GetSecrets(Kitchen.SaltSoup(), coin.ScriptPubKey).Single();
				var secret = hdKey.PrivateKey.GetBitcoinSecret(Keymanager.GetNetwork());
				if (hdKey.PrivateKey.PubKey.WitHash.ScriptPubKey != coin.ScriptPubKey)
				{
					throw new InvalidOperationException("The key cannot generate the utxo scriptpubkey. This could happen if the wallet password is not the correct one.");
				}
				aliceClients.Add(new AliceClient(roundState.Id, aliceArenaClient, coin, roundState.FeeRate, secret));
			}
			return aliceClients;
		}

		private async Task<ImmutableArray<AliceClient>> RegisterCoinsAsync(IEnumerable<AliceClient> aliceClients, CancellationToken cancellationToken)
		{
			async Task<AliceClient?> RegisterInputTask(AliceClient aliceClient)
			{
				var smartCoin = Coins.Single(x => x.Coin == aliceClient.Coin);
				try
				{
					await aliceClient.RegisterInputAsync(cancellationToken).ConfigureAwait(false);
					smartCoin.CoinJoinInProgress = true;
					return aliceClient;
				}
				catch (System.Net.Http.HttpRequestException ex)
				{
					if (ex.InnerException is WabiSabiProtocolException wpe)
					{
						switch (wpe.ErrorCode)
						{
							case WabiSabiProtocolErrorCode.InputSpent:
								smartCoin.SpentAccordingToBackend = true;
								Logger.LogInfo($"{smartCoin.Coin.Outpoint} is spent according to the backend. The wallet is not fully synchronized or corrupted.");
								break;
							case WabiSabiProtocolErrorCode.InputBanned:
								smartCoin.BannedUntilUtc = DateTimeOffset.UtcNow.AddDays(1);
								smartCoin.SetIsBanned();
								Logger.LogInfo($"{smartCoin.Coin.Outpoint} is banned.");
								break;
							case WabiSabiProtocolErrorCode.InputNotWhitelisted:
								smartCoin.SpentAccordingToBackend = false;
								Logger.LogInfo($"{smartCoin.Coin.Outpoint} cannot be registered in the blame round.");
								break;
							case WabiSabiProtocolErrorCode.AliceAlreadyRegistered:
								Logger.LogInfo($"{smartCoin.Coin.Outpoint} was already registered.");
								return aliceClient;
							case WabiSabiProtocolErrorCode.WrongPhase:
								return null; // The coin didn't get it and arrived too late to the party.
						}
					}
					Logger.LogInfo($"{smartCoin.Coin.Outpoint} registration failed with {ex}.");
					return null;
				}
			}

			var registerRequests = aliceClients.Select(RegisterInputTask);
			await Task.WhenAll(registerRequests).ConfigureAwait(false);

			return registerRequests
				.Where(x => x.Result is not null)
				.Select(x => x.Result!)
				.ToImmutableArray();
		}

		private IEnumerable<Money> DecomposeAmounts(IEnumerable<Coin> coins, FeeRate feeRate, Money minimumOutputAmount)
		{
			GreedyDecomposer greedyDecomposer = new(StandardDenomination.Values.Where(x => x >= minimumOutputAmount));
			var sum = coins.Sum(c => c.EffectiveValue(feeRate));
			return greedyDecomposer.Decompose(sum, feeRate.GetFee(31));
		}

		private BobClient CreateBobClient(RoundState roundState)
		{
			return new BobClient(
				roundState.Id,
				new(
					roundState.CreateAmountCredentialClient(SecureRandom),
					roundState.CreateVsizeCredentialClient(SecureRandom),
					ArenaRequestHandler));
		}

		private bool SanityCheck(IEnumerable<TxOut> expectedOutputs, Transaction unsignedCoinJoinTransaction)
		{
			var coinJoinOutputs = unsignedCoinJoinTransaction.Outputs.Select(o => (o.Value, o.ScriptPubKey));
			var expectedOutputTuples = expectedOutputs.Select(o => (o.Value, o.ScriptPubKey));
			return coinJoinOutputs.IsSuperSetOf(expectedOutputTuples);
		}

		private async Task SignTransactionAsync(IEnumerable<AliceClient> aliceClients, Transaction unsignedCoinJoinTransaction, CancellationToken cancellationToken)
		{
			async Task<AliceClient?> SignTransactionTask(AliceClient aliceClient)
			{
				try
				{
					await aliceClient.SignTransactionAsync(unsignedCoinJoinTransaction, cancellationToken).ConfigureAwait(false);
					return aliceClient;
				}
				catch (Exception e)
				{
					Logger.LogWarning($"Round ({aliceClient.RoundId}), Alice ({{aliceClient.AliceId}}): {nameof(AliceClient.SignTransactionAsync)} failed, reason:'{e}'.");
					return default;
				}
			}

			var signingRequests = aliceClients.Select(SignTransactionTask);
			await Task.WhenAll(signingRequests).ConfigureAwait(false);
		}

		private async Task ReadyToSignAsync(IEnumerable<AliceClient> aliceClients, CancellationToken cancellationToken)
		{
			async Task ReadyToSignTask(AliceClient aliceClient)
			{
				await aliceClient.ReadyToSignAsync(cancellationToken).ConfigureAwait(false);
			}

			var readyRequests = aliceClients.Select(ReadyToSignTask);
			await Task.WhenAll(readyRequests).ConfigureAwait(false);
		}

		private ImmutableList<SmartCoin> SelectCoinsForRound(IEnumerable<SmartCoin> coins, RoundState roundState) =>
			coins
				.Where(x => roundState.CoinjoinState.Parameters.AllowedInputAmounts.Contains(x.Amount) )
				.Where(x => roundState.CoinjoinState.Parameters.AllowedInputTypes.Any(t => x.ScriptPubKey.IsScriptType(t)))
				.OrderBy(x => x.HdPubKey.AnonymitySet)
				.ThenByDescending(x => x.Amount)
				.Take(MaxInputsRegistrableByWallet)
				.ToImmutableList();
	}
}
