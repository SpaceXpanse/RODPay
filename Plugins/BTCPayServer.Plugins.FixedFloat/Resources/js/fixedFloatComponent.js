Vue.component("FixedFloat", {
    props: ["toCurrency", "toCurrencyDue", "toCurrencyAddress"], methods: {
    },
    data: function () {
        return {
            shown: false
        }
    }
    , computed: {
        url: function () {
            let settleMethodId = "";
            if (this.toCurrency.endsWith('LightningLike') || this.toCurrency.endsWith('LNURLPay')) {
                settleMethodId = "BTCLN";
            } else {
                settleMethodId = this.toCurrency
                    .replace('_BTCLike', '')
                    .replace('_MoneroLike', '')
                    .replace('_ZcashLike', '')
                    .toUpperCase();
            }
            const topup = this.$parent.srvModel.isUnsetTopUp;
            return "https://widget.fixedfloat.com/?" +
                `to=${settleMethodId}` + 
                "&lockReceive=true&ref=fkbyt39c" +
                `&address=${this.toCurrencyAddress}` +
                (topup? "" : `&lockType=true&hideType=true&lockAmount=true&toAmount=${this.toCurrencyDue}` );

        }
    }
});
