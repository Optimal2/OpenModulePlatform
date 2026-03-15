// File: OpenModulePlatform.Portal/Pages/Admin/ArtifactEdit.cshtml.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Pages.Admin;
public sealed class ArtifactEditModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo; private static readonly Regex ShaPattern = new("^[A-Fa-f0-9]{64}$", RegexOptions.Compiled);
    public ArtifactEditModel(IOptions<WebAppOptions> options, RbacService rbac, OmpAdminRepository repo) : base(options, rbac) => _repo = repo;
    [BindProperty] public InputModel Input { get; set; } = new(); public IReadOnlyList<OptionItem> AppOptions { get; private set; } = []; public IReadOnlyList<OptionItem> PackageTypeOptions { get; } = new[] { Opt("folder","Folder"), Opt("zip","Zip"), Opt("nupkg","NuGet package") }; [TempData] public string? StatusMessage { get; set; } public bool IsCreate => Input.ArtifactId == 0;
    public async Task<IActionResult> OnGet(int? id, CancellationToken ct) { var guard = await RequirePortalAdminAsync(ct); if (guard is not null) return guard; await LoadAsync(ct); SetTitles(id.HasValue ? "Edit artifact" : "Create artifact"); if (id.HasValue) { var row = await _repo.GetArtifactAsync(id.Value, ct); if (row is null) return NotFound(); Input = new InputModel { ArtifactId = row.ArtifactId, AppId = row.AppId, Version = row.Version, PackageType = row.PackageType, TargetName = row.TargetName, RelativePath = row.RelativePath, Sha256 = row.Sha256, IsEnabled = row.IsEnabled }; } else { Input.PackageType = "folder"; Input.IsEnabled = true; } return Page(); }
    public async Task<IActionResult> OnPost(CancellationToken ct) { var guard = await RequirePortalAdminAsync(ct); if (guard is not null) return guard; await LoadAsync(ct); SetTitles(IsCreate ? "Create artifact" : "Edit artifact"); ValidateInput(); if (!ModelState.IsValid) return Page(); try { var id = await _repo.SaveArtifactAsync(new ArtifactEditData { ArtifactId = Input.ArtifactId, AppId = Input.AppId, Version = Input.Version.Trim(), PackageType = Input.PackageType, TargetName = Clean(Input.TargetName), RelativePath = Clean(Input.RelativePath), Sha256 = Clean(Input.Sha256), IsEnabled = Input.IsEnabled }, ct); StatusMessage = IsCreate ? "Artifact created." : "Artifact updated."; return RedirectToPage("~/admin/artifactedit", new { id }); } catch (SqlException ex) { ModelState.AddModelError(string.Empty, ToFriendlySqlMessage(ex, "The artifact could not be saved.")); return Page(); } }
    public async Task<IActionResult> OnPostDelete(CancellationToken ct) { var guard = await RequirePortalAdminAsync(ct); if (guard is not null) return guard; try { await _repo.DeleteArtifactAsync(Input.ArtifactId, ct); StatusMessage = "Artifact deleted."; return RedirectToPage("~/admin/artifacts"); } catch (SqlException ex) { await LoadAsync(ct); SetTitles("Edit artifact"); ModelState.AddModelError(string.Empty, ToFriendlySqlMessage(ex, "The artifact could not be deleted.")); return Page(); } }
    private async Task LoadAsync(CancellationToken ct) => AppOptions = await _repo.GetAppOptionsAsync(ct);
    private void ValidateInput() { if (Input.AppId <= 0) ModelState.AddModelError(nameof(Input.AppId), "Select an app."); if (string.IsNullOrWhiteSpace(Input.Version)) ModelState.AddModelError(nameof(Input.Version), "Version is required."); if (!string.IsNullOrWhiteSpace(Input.Sha256) && !ShaPattern.IsMatch(Input.Sha256)) ModelState.AddModelError(nameof(Input.Sha256), "SHA-256 must be 64 hexadecimal characters."); }
    private static OptionItem Opt(string value, string label) => new() { Value = value, Label = label }; private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim(); private static string ToFriendlySqlMessage(SqlException ex, string fallback) => ex.Number == 547 ? "Delete or update dependent rows first." : fallback;
    public sealed class InputModel { public int ArtifactId { get; set; } [Required, Display(Name = "App")] public int AppId { get; set; } [Required, StringLength(50)] public string Version { get; set; } = string.Empty; [Required, StringLength(50), Display(Name = "Package type")] public string PackageType { get; set; } = string.Empty; [StringLength(100), Display(Name = "Target name")] public string? TargetName { get; set; } [StringLength(400), Display(Name = "Relative path")] public string? RelativePath { get; set; } [StringLength(128), Display(Name = "SHA-256")] public string? Sha256 { get; set; } [Display(Name = "Enabled")] public bool IsEnabled { get; set; } = true; }
}
