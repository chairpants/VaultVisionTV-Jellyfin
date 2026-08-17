using Jellyfin.Plugin.VaultVisionTV.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VaultVisionTV.Api;

// M3U / XMLTV / live-stream endpoints — what Jellyfin's Live TV "M3U Tuner"
// source and XMLTV guide provider are pointed at. [AllowAnonymous] because
// Jellyfin's own server fetches these itself, not a logged-in client.
[ApiController]
[AllowAnonymous]
[Route("VaultVisionTV/iptv")]
public class IptvController : ControllerBase
{
    private readonly EpgService _epg;
    private readonly StreamService _stream;

    public IptvController(EpgService epg, StreamService stream)
    {
        _epg = epg;
        _stream = stream;
    }

    [HttpGet("channels.m3u")]
    public ActionResult GetM3u()
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Content(_epg.GenerateM3u(baseUrl), "audio/x-mpegurl");
    }

    [HttpGet("epg.xml")]
    public ActionResult GetEpg()
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var guideDays = Plugin.Instance?.Configuration.GuideDays ?? 3;
        var xml = _epg.GenerateXmlTv(baseUrl, DateTime.Now, guideDays);
        return Content(xml, "application/xml; charset=utf-8");
    }

    [HttpHead("stream/{channelNumber}")]
    [HttpGet("stream/{channelNumber}")]
    public async Task Stream(int channelNumber, CancellationToken cancellationToken)
    {
        if (HttpMethods.IsHead(Request.Method))
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "video/mp2t";
            return;
        }

        Response.ContentType = "video/mp2t";
        Response.Headers.CacheControl = "no-cache";

        var result = await _stream.StreamChannelAsync(channelNumber, Response.Body, cancellationToken).ConfigureAwait(false);

        if (result != StreamService.StreamResult.Ok && !Response.HasStarted)
        {
            Response.StatusCode = result == StreamService.StreamResult.ChannelNotFound
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status503ServiceUnavailable;
        }
    }
}
