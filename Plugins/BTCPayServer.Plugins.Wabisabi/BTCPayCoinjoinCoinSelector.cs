﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using Microsoft.Extensions.Logging;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Extensions;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.Wallets;

namespace BTCPayServer.Plugins.Wabisabi;

public class BTCPayCoinjoinCoinSelector: IRoundCoinSelector
{
    private readonly BTCPayWallet _wallet;
    private readonly ILogger _logger;

    public BTCPayCoinjoinCoinSelector(BTCPayWallet wallet, ILogger logger)
    {
        _wallet = wallet;
        _logger = logger;
    }
    public async Task<ImmutableList<SmartCoin>> SelectCoinsAsync(IEnumerable<SmartCoin> coinCandidates,
        UtxoSelectionParameters utxoSelectionParameters,
        Money liquidityClue, SecureRandom secureRandom)
    {
        var payments= await _wallet.DestinationProvider.GetPendingPaymentsAsync(utxoSelectionParameters);

        SubsetSolution bestSolution = null;
        for (int i = 0; i < 100; i++)
        {
            var minCoins = new Dictionary<AnonsetType, int>();
            if (_wallet.RedCoinIsolation)
            {
                minCoins.Add(AnonsetType.Red, 1);
            }
            var solution = SelectCoinsInternal(utxoSelectionParameters, coinCandidates, payments,  Random.Shared.Next(10,31), 
                minCoins,  new Dictionary<AnonsetType, int>()
            {
                    
                {AnonsetType.Red, 1},
                {AnonsetType.Orange, 1},
                {AnonsetType.Green, 1}
            },true);
            if (bestSolution is null || solution.Score() > bestSolution.Score())
            {
                bestSolution = solution;
            }
        }
        _logger.LogInformation(bestSolution.ToString());
        return bestSolution.Coins.ToImmutableList();
    }

    private SubsetSolution SelectCoinsInternal(UtxoSelectionParameters utxoSelectionParameters,
        IEnumerable<SmartCoin> coins, IEnumerable<PendingPayment> pendingPayments,
        int maxCoins,
        Dictionary<AnonsetType, int> maxPerType, Dictionary<AnonsetType, int> idealMinimumPerType,
        bool consolidationMode)
        {
            var stopwatch = Stopwatch.StartNew();
            
            // Sort the coins by their anon score and then bydescending order their value, and then slightly randomize in 2 ways:
            //attempt to shift coins that comes from the same tx AND also attempt to shift coins based on percentage probability
            var remainingCoins = SlightlyShiftOrder(RandomizeCoins(
                coins.OrderBy(coin => coin.CoinColor(_wallet.AnonScoreTarget)).ThenByDescending(x => x.EffectiveValue(utxoSelectionParameters.MiningFeeRate,utxoSelectionParameters.CoordinationFeeRate)).ToList()), 10);
            var remainingPendingPayments = new List<PendingPayment>(pendingPayments);
            var solution = new SubsetSolution(remainingPendingPayments.Count, _wallet.AnonScoreTarget, utxoSelectionParameters);

            while (remainingCoins.Any())
            {
                var coinColorCount = solution.SortedCoins.ToDictionary(pair => pair.Key, pair => pair.Value.Length);

                var predicate = new Func<SmartCoin, bool>(_ => true);
                foreach (var coinColor in idealMinimumPerType.ToShuffled())
                {
                    if (coinColor.Value != 0)
                    {
                        coinColorCount.TryGetValue(coinColor.Key, out var currentCoinColorCount);
                        if (currentCoinColorCount < coinColor.Value)
                        {
                            predicate = coin1 => coin1.CoinColor(_wallet.AnonScoreTarget) == coinColor.Key;
                            break;
                        }
                    }
                    else
                    {
                        //if the ideal amount = 0, then we should de-prioritize.
                        predicate = coin1 => coin1.CoinColor(_wallet.AnonScoreTarget)  != coinColor.Key;
                        break;
                    }

                }
                var coin = remainingCoins.FirstOrDefault(predicate)?? remainingCoins.First();
                var color = coin.CoinColor(_wallet.AnonScoreTarget);
                // If the selected coins list is at its maximum size, break out of the loop
                if (solution.Coins.Count == maxCoins)
                {
                    break;
                }

                remainingCoins.Remove(coin);
                if (maxPerType.TryGetValue(color, out var maxColor) &&
                    solution.Coins.Count(coin1 =>coin1.CoinColor(_wallet.AnonScoreTarget)  == color) == maxColor)
                {
                    
                    continue;
                }

                solution.Coins.Add(coin);

                // Loop through the pending payments and handle each payment by subtracting the payment amount from the total value of the selected coins
                var potentialPayments = remainingPendingPayments
                    .Where(payment => payment.ToTxOut().EffectiveCost(utxoSelectionParameters.MiningFeeRate).ToDecimal(MoneyUnit.BTC) <= solution.LeftoverValue).ToShuffled();
                    
                while (potentialPayments.Any())
                {
                    var payment = potentialPayments.First();
                    solution.HandledPayments.Add(payment);
                    remainingPendingPayments.Remove(payment);
                    potentialPayments = remainingPendingPayments.Where(payment => payment.ToTxOut().EffectiveCost(utxoSelectionParameters.MiningFeeRate).ToDecimal(MoneyUnit.BTC) <= solution.LeftoverValue).ToShuffled();
                }

                if (!remainingPendingPayments.Any())
                {
                    var rand = Random.Shared.Next(1, 101);
                    //let's check how many coins we are allowed to add max and how many we added, and use that percentage as the random chance of not adding it.
                    // if max coins = 20, and current coins  = 5 then 5/20 = 0.25 * 100 = 25
                    var maxCoinCapacityPercentage = Math.Floor((solution.Coins.Count / (decimal)maxCoins) * 100);
                    //aggressively attempt to reach max coin target if consolidation mode is on
                    var chance = consolidationMode ? 10 : 100 - maxCoinCapacityPercentage;

                    if (chance <= rand)
                    {
                        break;

                    }

                }
            }
            stopwatch.Stop();
            solution.TimeElapsed = stopwatch.Elapsed;
            return solution;
        }
    
    static List<T> SlightlyShiftOrder<T>(List<T> list, int chanceOfShiftPercentage)
    {
        // Create a random number generator
        var rand = new Random();
        List<T> workingList = new List<T>(list);
// Loop through the coins and determine whether to swap the positions of two consecutive coins in the list
        for (int i = 0; i < workingList.Count() - 1; i++)
        {
            // If a random number between 0 and 1 is less than or equal to 0.1, swap the positions of the current and next coins in the list
            if (rand.NextDouble() <= ((double)chanceOfShiftPercentage / 100))
            {
                // Swap the positions of the current and next coins in the list
                (workingList[i], workingList[i + 1]) = (workingList[i + 1], workingList[i]);
            }
        }

        return workingList;
    }

    private List<SmartCoin> RandomizeCoins(List<SmartCoin> coins)
    {
        var remainingCoins = new List<SmartCoin>(coins);
        var workingList = new List<SmartCoin>();
        while (remainingCoins.Any())
        {
            var currentCoin = remainingCoins.First();
            remainingCoins.RemoveAt(0);
            var lastCoin = workingList.LastOrDefault();
            if (lastCoin is null || currentCoin.CoinColor(_wallet.AnonScoreTarget) == AnonsetType.Green || !remainingCoins.Any() ||
                (remainingCoins.Count == 1 && remainingCoins.First().TransactionId == currentCoin.TransactionId) ||
                lastCoin.TransactionId != currentCoin.TransactionId || Random.Shared.Next(0, 10) < 5)
            {
                workingList.Add(currentCoin);
            }
            else
            {
                remainingCoins.Insert(1, currentCoin);
            }
        }


        return workingList.ToList();
    }

}

public static class SmartCoinExtensions
{
    public static AnonsetType CoinColor(this SmartCoin coin, int anonsetTarget)
    {
        return coin.AnonymitySet <= 1 ? AnonsetType.Red :
            coin.AnonymitySet >= anonsetTarget ? AnonsetType.Green : AnonsetType.Orange;
    }
}

public enum AnonsetType
{
    Red,
    Orange,
    Green
}

    public class SubsetSolution:IEquatable<SubsetSolution>
    {
        private readonly UtxoSelectionParameters _utxoSelectionParameters;

        public SubsetSolution(int totalPaymentsGross, int anonsetTarget, UtxoSelectionParameters utxoSelectionParameters )
        {
            _utxoSelectionParameters = utxoSelectionParameters;
            TotalPaymentsGross = totalPaymentsGross;
            AnonsetTarget = anonsetTarget;
        }
        
        public TimeSpan TimeElapsed { get; set; }
        public List<SmartCoin> Coins { get; set; } = new();
        public List<PendingPayment> HandledPayments { get; set; } = new();

        public decimal TotalValue => Coins.Sum(coin =>  coin.EffectiveValue(_utxoSelectionParameters.MiningFeeRate, _utxoSelectionParameters.CoordinationFeeRate).ToDecimal(MoneyUnit.BTC));

        public Dictionary<AnonsetType, SmartCoin[]> SortedCoins =>
            Coins.GroupBy(coin => coin.CoinColor(AnonsetTarget)).ToDictionary(coins => coins.Key, coins => coins.ToArray());

        public int TotalPaymentsGross { get;}
        public int AnonsetTarget { get; }
        public decimal TotalPaymentCost => HandledPayments.Sum(payment => payment.ToTxOut().EffectiveCost(_utxoSelectionParameters.MiningFeeRate).ToDecimal(MoneyUnit.BTC));
        public decimal LeftoverValue => TotalValue - TotalPaymentCost;

        public decimal Score()
        {
            var score = 0m;

            decimal ComputeCoinScore(List<SmartCoin> coins)
            {
                var w = 0m;
                foreach (var smartCoin in coins)
                {
                    var val = smartCoin.EffectiveValue(_utxoSelectionParameters.MiningFeeRate,
                        _utxoSelectionParameters.CoordinationFeeRate).ToDecimal(MoneyUnit.BTC);
                    if (smartCoin.AnonymitySet <= 0)
                    {
                        w += val;
                    }
                    else
                    {
                        w += val/ (decimal) smartCoin.AnonymitySet;
                    }
                }

                return w; // / (coins.Count == 0 ? 1 : coins.Count);
            }

            decimal ComputePaymentScore(List<PendingPayment> pendingPayments)
            {
                return  TotalPaymentsGross == 0? 100: (pendingPayments.Count / (decimal)TotalPaymentsGross)*100;
            }

            score += ComputeCoinScore(Coins);
            score += ComputePaymentScore(HandledPayments);

            return score;
        }


        public string GetId()
        {
            return string.Join("-",
                Coins.OrderBy(coin => coin.Outpoint).Select(coin => coin.Outpoint.ToString())
                    .Concat(HandledPayments.OrderBy(arg => arg.Value).Select(p => p.Value.ToString())));

        }
        
        public override string ToString()
        {
            var sb = new StringBuilder();

            var sc = SortedCoins;
            sc.TryGetValue(AnonsetType.Green, out var gcoins);
            sc.TryGetValue(AnonsetType.Orange, out var ocoins);
            sc.TryGetValue(AnonsetType.Red, out var rcoins);
            sb.AppendLine(
                $"Solution total coins:{Coins.Count} R:{rcoins?.Length??0} O:{ocoins?.Length??0} G:{gcoins?.Length??0} AL:{GetAnonLoss(Coins)} total value: {TotalValue} total payments:{TotalPaymentCost} leftover: {LeftoverValue} score: {Score()} Compute time: {TimeElapsed} ");
            sb.AppendLine($"Used coins: {string.Join(", ", Coins.Select(coin => coin.Outpoint + " " + coin.Amount.ToString() + " A"+coin.AnonymitySet))}");
            sb.AppendLine($"handled payments: {string.Join(", ", HandledPayments.Select(p => p.Value))}");
            return sb.ToString();
        }

        public bool Equals(SubsetSolution? other)
        {
            return GetId() == other?.GetId();
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((SubsetSolution) obj);
        }
        
        private static decimal GetAnonLoss<TCoin>(IEnumerable<TCoin> coins)
            where TCoin : SmartCoin
        {
            double minimumAnonScore = coins.Min(x => x.AnonymitySet);
            var rawSum = coins.Sum(x => x.Amount);
            return coins.Sum(x => ((decimal)x.AnonymitySet - (decimal)minimumAnonScore) * x.Amount.ToDecimal(MoneyUnit.BTC)) / rawSum;
        }
    }