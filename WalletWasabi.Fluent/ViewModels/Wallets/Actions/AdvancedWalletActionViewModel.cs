﻿using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Actions
{
	[NavigationMetaData(
		Title = "Advanced",
		Caption = "",
		IconName = "wallet_action_advanced",
		NavBarPosition = NavBarPosition.None,
		NavigationTarget = NavigationTarget.HomeScreen)]
	public partial class AdvancedWalletActionViewModel : WalletActionViewModel
	{
		public AdvancedWalletActionViewModel(WalletViewModelBase wallet) : base(wallet)
		{
		}
	}
}