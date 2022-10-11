using System;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using LNURL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;

namespace BTCPayServer.Plugins.NFC
{
    [Route("plugins/NFC")]
    public class NFCController : Controller
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public NFCController(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public class SubmitRequest
        {
            public string Lnurl { get; set; }
            public string Destination { get; set; }
        }

        [AllowAnonymous]
        public async Task<IActionResult> SubmitLNURLWithdrawForInvoice([FromBody] SubmitRequest request)
        {
            var uri = LNURL.LNURL.Parse(request.Lnurl, out var tag);
            if (uri is null)
            {
                return BadRequest();
            }

            if (!string.IsNullOrEmpty(tag) && tag.Equals("withdrawRequest"))
            {
                return BadRequest();
            }

            var httpClient = _httpClientFactory.CreateClient(uri.IsOnion()
                ? "LightningLikePayoutHandlerOnionNamedClient"
                : "LightningLikePayoutHandlerClearnetNamedClient");
            var info = (await
                LNURL.LNURL.FetchInformation(uri, "withdrawRequest", httpClient)) as LNURLWithdrawRequest;
            if (info is null)
            {
                return BadRequest();
            }

            httpClient = _httpClientFactory.CreateClient(info.Callback.IsOnion()
                ? "LightningLikePayoutHandlerOnionNamedClient"
                : "LightningLikePayoutHandlerClearnetNamedClient");

            try
            {
                var destinationuri = LNURL.LNURL.Parse(request.Destination, out string _);

                var destinfo = (await
                    LNURL.LNURL.FetchInformation(destinationuri, "payRequest", httpClient)) as LNURLPayRequest;

                if (destinfo is null)
                {
                    return BadRequest();
                }

                httpClient = _httpClientFactory.CreateClient(destinfo.Callback.IsOnion()
                    ? "LightningLikePayoutHandlerOnionNamedClient"
                    : "LightningLikePayoutHandlerClearnetNamedClient");
                var destCallback = await destinfo.SendRequest(destinfo.MinSendable, Network.Main, httpClient);
                request.Destination = destCallback.Pr;
            }
            catch (Exception e)
            {
            }

            var result = await info.SendRequest(request.Destination, httpClient);
            if (result.Status.Equals("ok", StringComparison.InvariantCultureIgnoreCase))
            {
                return Ok();
            }

            return BadRequest(result);
        }
    }
}
