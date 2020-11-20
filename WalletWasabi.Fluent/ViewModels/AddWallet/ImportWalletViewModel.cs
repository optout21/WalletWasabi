﻿using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using NBitcoin;
using Newtonsoft.Json.Linq;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.AddWallet
{
	public class ImportWalletViewModel : RoutableViewModel
	{
		private const string _walletExistsErrorMessage = "Wallet with the same fingerprint already exists!";
		private string WalletName { get; }
		private WalletManager WalletManager { get; }

		public ImportWalletViewModel(NavigationStateViewModel navigationState, string walletName, WalletManager walletManager) : base(navigationState, NavigationTarget.DialogScreen)
		{
			WalletName = walletName;
			WalletManager = walletManager;

			ImportWallet();
		}

		private async void ImportWallet()
		{
			var filePath = await GetFilePath();

			// Dialog canceled.
			if (filePath is null)
			{
				return;
			}

			var walletFullPath = WalletManager.WalletDirectories.GetWalletFilePaths(WalletName).walletFilePath;

			try
			{
				string jsonString = await File.ReadAllTextAsync(filePath);
				var jsonWallet = JObject.Parse(jsonString);

				// TODO: Better logic to distinguish wallets.
				// If Count <= 3 then it is a possible Coldcard json otherwise possible Wasabi json
				KeyManager km = jsonWallet.Count <= 3 ? GetKeyManagerByColdcardJson(jsonWallet, walletFullPath) : GetKeyManagerByWasabiJson(filePath, walletFullPath);

				WalletManager.AddWallet(km);
				ClearNavigation();
			}
			catch (Exception ex)
			{
				// TODO: Notify the user
				Logger.LogError(ex);
			}
		}

		private bool IsWalletExists(HDFingerprint? fingerprint) => WalletManager.GetWallets().Any(x => fingerprint is { } && x.KeyManager.MasterFingerprint == fingerprint);

		private KeyManager GetKeyManagerByWasabiJson(string filePath, string walletFullPath)
		{
			var km = KeyManager.FromFile(filePath);

			if (IsWalletExists(km.MasterFingerprint))
			{
				throw new InvalidOperationException(_walletExistsErrorMessage);
			}

			km.SetFilePath(walletFullPath);

			return km;
		}

		private KeyManager GetKeyManagerByColdcardJson(JObject jsonWallet, string walletFullPath)
		{
			var xpubString = jsonWallet["ExtPubKey"].ToString();
			var mfpString = jsonWallet["MasterFingerprint"].ToString();

			// https://github.com/zkSNACKs/WalletWasabi/pull/1663#issuecomment-508073066
			// Coldcard 2.1.0 improperly implemented Wasabi skeleton fingerprint at first, so we must reverse byte order.
			// The solution was to add a ColdCardFirmwareVersion json field from 2.1.1 and correct the one generated by 2.1.0.
			var coldCardVersionString = jsonWallet["ColdCardFirmwareVersion"]?.ToString();
			var reverseByteOrder = false;
			if (coldCardVersionString is null)
			{
				reverseByteOrder = true;
			}
			else
			{
				Version coldCardVersion = new (coldCardVersionString);

				if (coldCardVersion == new Version("2.1.0")) // Should never happen though.
				{
					reverseByteOrder = true;
				}
			}

			var bytes = ByteHelpers.FromHex(Guard.NotNullOrEmptyOrWhitespace(nameof(mfpString), mfpString, trim: true));
			HDFingerprint mfp = reverseByteOrder ? new HDFingerprint(bytes.Reverse().ToArray()) : new HDFingerprint(bytes);

			if (IsWalletExists(mfp))
			{
				throw new InvalidOperationException(_walletExistsErrorMessage);
			}

			ExtPubKey extPubKey = NBitcoinHelpers.BetterParseExtPubKey(xpubString);

			return KeyManager.CreateNewHardwareWalletWatchOnly(mfp, extPubKey, walletFullPath);
		}

		private async Task<string?> GetFilePath()
		{
			var ofd = new OpenFileDialog
			{
				AllowMultiple = false,
				Title = "Import wallet file",
			};

			var window = ((IClassicDesktopStyleApplicationLifetime)Application.Current.ApplicationLifetime).MainWindow;
			var selected = await ofd.ShowAsync(window);

			if (selected is { } && selected.Any())
			{
				return selected.First();
			}

			return null;
		}
	}
}