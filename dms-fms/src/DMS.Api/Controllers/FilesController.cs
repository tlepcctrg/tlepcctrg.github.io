using DMS.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Api.Controllers;

[ApiController]
[Route("api/files")]
public sealed class FilesController : ControllerBase
{
    private readonly FileUploadService _uploadService;

    public FilesController(FileUploadService uploadService) => _uploadService = uploadService;

    /// <summary>
    /// Direct-to-storage upload step 1: returns a pre-signed PUT URL (or a
    /// set of pre-signed part URLs for multipart) that the client uploads
    /// bytes to directly - DMS never proxies the payload.
    /// </summary>
    [HttpPost("init-upload")]
    public async Task<IActionResult> InitUpload([FromBody] InitUploadRequestDto request, CancellationToken ct)
    {
        var result = await _uploadService.InitUploadAsync(
            request.TenantId, request.FolderId, request.FileName, request.ContentType,
            request.ExpectedSize, request.Multipart, request.PartCount, request.OwnerId, ct);

        return Ok(result);
    }

    /// <summary>
    /// Direct-to-storage upload step 2: called by the client after the
    /// binary payload has been uploaded directly to object storage.
    /// </summary>
    [HttpPost("{id}/commit")]
    public async Task<IActionResult> Commit(string id, [FromBody] CommitUploadRequestDto request, CancellationToken ct)
    {
        var result = await _uploadService.CommitAsync(request.TenantId, id, request.ExpectedSha256, ct);
        return result.Success ? Ok(result) : BadRequest(result.Message);
    }

    /// <summary>Mints a time-boxed pre-signed GET URL; DMS never streams bytes.</summary>
    [HttpGet("{id}/download-url")]
    public async Task<IActionResult> GetDownloadUrl(string id, [FromQuery] string tenantId, CancellationToken ct)
    {
        var url = await _uploadService.GetDownloadUrlAsync(tenantId, id, ct);
        return url is null ? NotFound() : Ok(new { downloadUrl = url });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] string tenantId, CancellationToken ct)
    {
        var deleted = await _uploadService.DeleteAsync(tenantId, id, ct);
        return deleted ? NoContent() : NotFound();
    }
}
