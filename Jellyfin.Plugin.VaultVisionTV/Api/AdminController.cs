using Jellyfin.Plugin.VaultVisionTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VaultVisionTV.Api;

// Admin-only actions for the config page — unlike IptvController, these need
// a real (elevated) Jellyfin session, not anonymous access.
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("VaultVisionTV")]
public class AdminController : ControllerBase
{
    private readonly CatalogService _catalog;

    public AdminController(CatalogService catalog)
    {
        _catalog = catalog;
    }

    [HttpPost("refresh-catalog")]
    public async Task<ActionResult> RefreshCatalog(CancellationToken cancellationToken)
    {
        await _catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { shows = _catalog.Current?.Shows.Count ?? 0, generatedAt = _catalog.Current?.GeneratedAt });
    }
}
