﻿using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments.PayJoin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalletWasabi.Tor.Socks5.Pool.Circuits;
using WalletWasabi.Userfacing;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Client.StatusChangedEvents;
using WalletWasabi.WebClients.Wasabi;

namespace BTCPayServer.Plugins.Wabisabi;

public class WabisabiCoordinatorManager : IWabisabiCoordinatorManager
{
    private readonly IUTXOLocker _utxoLocker;
    private readonly ILogger _logger;
    public string CoordinatorDisplayName { get; }
    public string CoordinatorName { get; set; }
    public Uri Coordinator { get; set; }
    public WalletProvider WalletProvider { get; }
    public HttpClientFactory WasabiHttpClientFactory { get; set; }
    public RoundStateUpdater RoundStateUpdater { get; set; }
    public WasabiCoordinatorStatusFetcher WasabiCoordinatorStatusFetcher { get; set; }
    public CoinJoinManager CoinJoinManager { get; set; }

    public WabisabiCoordinatorManager(string coordinatorDisplayName,string coordinatorName, Uri coordinator, ILoggerFactory loggerFactory, IServiceProvider serviceProvider, IUTXOLocker utxoLocker)
    {
        _utxoLocker = utxoLocker;
        var config = serviceProvider.GetService<IConfiguration>();
        var socksEndpoint = config.GetValue<string>("socksendpoint");
        EndPointParser.TryParse(socksEndpoint,9050, out var torEndpoint);
        if (torEndpoint is not null && torEndpoint is DnsEndPoint dnsEndPoint)
        {
            torEndpoint = new IPEndPoint(Dns.GetHostAddresses(dnsEndPoint.Host).First(), dnsEndPoint.Port);
        }
        CoordinatorDisplayName = coordinatorDisplayName;
        CoordinatorName = coordinatorName;
        Coordinator = coordinator;
        WalletProvider = ActivatorUtilities.CreateInstance<WalletProvider>(serviceProvider);
        WalletProvider.UtxoLocker = _utxoLocker;
        WalletProvider.CoordinatorName = CoordinatorName;
        _logger = loggerFactory.CreateLogger(coordinatorName);
        WasabiHttpClientFactory = new HttpClientFactory(torEndpoint, () => Coordinator);
        var roundStateUpdaterCircuit = new PersonCircuit();
        var roundStateUpdaterHttpClient =
            WasabiHttpClientFactory.NewHttpClient(Mode.SingleCircuitPerLifetime, roundStateUpdaterCircuit);
        RoundStateUpdater = new RoundStateUpdater(TimeSpan.FromSeconds(5),
            new WabiSabiHttpApiClient(roundStateUpdaterHttpClient));
        WasabiCoordinatorStatusFetcher = new WasabiCoordinatorStatusFetcher(WasabiHttpClientFactory.SharedWasabiClient,
            loggerFactory.CreateLogger<WasabiCoordinatorStatusFetcher>());
        CoinJoinManager = new CoinJoinManager(WalletProvider, RoundStateUpdater, WasabiHttpClientFactory,
            WasabiCoordinatorStatusFetcher, "CoinJoinCoordinatorIdentifier");
        CoinJoinManager.StatusChanged += OnStatusChanged;
        
        
    }

    public async Task StopWallet(string walletName)
    {
        await CoinJoinManager.StopAsyncByName(walletName, CancellationToken.None);
    }

    private void OnStatusChanged(object sender, StatusChangedEventArgs e)
    {
        
        switch (e)
        {
            case CoinJoinStatusEventArgs coinJoinStatusEventArgs:
                _logger.LogInformation(coinJoinStatusEventArgs.CoinJoinProgressEventArgs.GetType().ToString() + "   :" +
                                       e.Wallet.WalletName);
                break;
            case CompletedEventArgs completedEventArgs:
                
                var result = completedEventArgs.CoinJoinResult;
                
                if (completedEventArgs.CompletionStatus == CompletionStatus.Success)
                {
                    Task.Run(async () =>
                    {
                        
                        var wallet = (BTCPayWallet) e.Wallet;
                        await wallet.RegisterCoinjoinTransaction(result);
                                    
                    });
                }
                else
                {
                    Task.Run(async () =>
                    {
                        // _logger.LogInformation("unlocking coins because round failed");
                        await _utxoLocker.TryUnlock(
                            result.RegisteredCoins.Select(coin => coin.Outpoint).ToArray());
                    });
                    break;
                }
                _logger.LogInformation("Coinjoin complete!   :" +
                                       e.Wallet.WalletName);
                break;
            case LoadedEventArgs loadedEventArgs:
                var stopWhenAllMixed = !((BTCPayWallet)loadedEventArgs.Wallet).BatchPayments;
               _ = CoinJoinManager.StartAsync(loadedEventArgs.Wallet, stopWhenAllMixed, false, CancellationToken.None);
                _logger.LogInformation( "Loaded wallet  :" + e.Wallet.WalletName + $"stopWhenAllMixed: {stopWhenAllMixed}");
                break;
            case StartErrorEventArgs errorArgs:
                _logger.LogInformation("Could not start wallet for coinjoin:" + errorArgs.Error.ToString() + "   :" + e.Wallet.WalletName);
                break;
            case StoppedEventArgs stoppedEventArgs:
                _logger.LogInformation("Stopped wallet for coinjoin: " + stoppedEventArgs.Reason + "   :" + e.Wallet.WalletName);
                break;
            default:
                _logger.LogInformation(e.GetType() + "   :" + e.Wallet.WalletName);
                break;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        
        RoundStateUpdater.StartAsync(cancellationToken);
        WasabiCoordinatorStatusFetcher.StartAsync(cancellationToken);
        CoinJoinManager.StartAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        RoundStateUpdater.StopAsync(cancellationToken);
        WasabiCoordinatorStatusFetcher.StopAsync(cancellationToken);
        CoinJoinManager.StopAsync(cancellationToken);
        return Task.CompletedTask;
    }
}
