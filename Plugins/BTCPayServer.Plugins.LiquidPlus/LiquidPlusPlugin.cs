﻿using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.CustomLiquidAssets.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.CustomLiquidAssets
{
    public class LiquidPlusPlugin : BaseBTCPayServerPlugin
    {
        public override string Identifier { get; } = "BTCPayServer.Plugins.LiquidPlus";
        public override string Name { get; } = "Liquid+";
        public override string Description { get; } = "Enhanced support for the liquid network.";

        public override void Execute(IServiceCollection services)
        {
            services.AddSingleton<IUIExtension>(new UIExtension("CustomLiquidAssetsNavExtension", "server-nav"));
            services.AddSingleton<IUIExtension>(new UIExtension("StoreNavLiquidExtension",  "store-nav"));
            services.AddSingleton<CustomLiquidAssetsRepository>();

            var originalImplementationFactory = services.Single(descriptor =>
                descriptor.Lifetime == ServiceLifetime.Singleton &&
                descriptor.ServiceType == typeof(BTCPayNetworkProvider));
            services.Replace(ServiceDescriptor.Singleton(provider =>
            {
                var _customLiquidAssetsRepository = provider.GetService<CustomLiquidAssetsRepository>();
                var _logger = provider.GetService<ILogger<LiquidPlusPlugin>>();
                var networkProvider =
                    (originalImplementationFactory.ImplementationInstance ??
                     originalImplementationFactory.ImplementationFactory.Invoke(provider)) as BTCPayNetworkProvider;
                if (networkProvider.Support("LBTC"))
                {
                    var settings = _customLiquidAssetsRepository.Get();
                    var template = networkProvider.GetNetwork<ElementsBTCPayNetwork>("LBTC");
                    var additionalNetworks = settings.Items.Select(configuration => new ElementsBTCPayNetwork()
                    {
                        CryptoCode = configuration.CryptoCode
                            .Replace("-", "")
                            .Replace("_", ""),
                        DefaultRateRules = configuration.DefaultRateRules ?? Array.Empty<string>(),
                        AssetId = uint256.Parse(configuration.AssetId),
                        Divisibility = configuration.Divisibility,
                        DisplayName = configuration.DisplayName,
                        CryptoImagePath = configuration.CryptoImagePath,
                        NetworkCryptoCode = template.NetworkCryptoCode,
                        DefaultSettings = template.DefaultSettings,
                        ElectrumMapping = template.ElectrumMapping,
                        BlockExplorerLink = template.BlockExplorerLink,
                        ReadonlyWallet = template.ReadonlyWallet,
                        SupportLightning = false,
                        SupportPayJoin = false,
                        ShowSyncSummary = false,
                        WalletSupported = template.WalletSupported,
                        LightningImagePath = "",
                        NBXplorerNetwork = template.NBXplorerNetwork,
                        CoinType = template.CoinType
                    });
                    var newCryptoCodes = settings.Items.Select(configuration => configuration.CryptoCode).ToArray();
                    _logger.LogInformation($"Loaded {newCryptoCodes.Length} " +
                                           $"{(!newCryptoCodes.Any()?string.Empty: $"({string.Join(',', newCryptoCodes)})")} additional liquid assets");
                    var newSupportedChains = networkProvider.GetAll().Select(b => b.CryptoCode).Concat(newCryptoCodes).ToArray();
                    return new BTCPayNetworkProviderOverride(networkProvider.NetworkType, additionalNetworks).Filter(newSupportedChains);
                }

                return networkProvider;
            }));
        }
    }

    public class BTCPayNetworkProviderOverride : BTCPayNetworkProvider
    {
        public BTCPayNetworkProviderOverride(ChainName networkType,
            IEnumerable<ElementsBTCPayNetwork> elementsBTCPayNetworks) : base(networkType)
        {
            foreach (ElementsBTCPayNetwork elementsBTCPayNetwork in elementsBTCPayNetworks)
            {
                _Networks.TryAdd(elementsBTCPayNetwork.CryptoCode.ToUpperInvariant(), elementsBTCPayNetwork);
            }
        }
    }
}
