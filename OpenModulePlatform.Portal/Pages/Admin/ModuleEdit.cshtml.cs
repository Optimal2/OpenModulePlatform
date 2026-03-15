// File: OpenModulePlatform.Portal/Pages/Admin/ModuleEdit.cshtml.cs
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
public sealed class ModuleEditModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo; private static readonly Regex KeyPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled); private static readonly Regex SchemaPattern = new("^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);
    public ModuleEditModel(IOptions<WebAppOptions> options, RbacService rbac, OmpAdminRepository repo) : base(options, rbac) => _repo = repo;
    [BindProperty] public InputModel Input { get; set; } = new();
    public IReadOnlyList<OptionItem> ModuleTypeOptions { get; } = new[] { Opt("PortalModule","Portal module"), Opt("WebAppModule","Web app module"), Opt("HostAppModule","Host app module") };
    [TempData] public string? StatusMessage { get; set; }
    public bool IsCreate => Input.ModuleId == 0;
    public async Task<IActionResult> OnGet(int? id, CancellationToken ct) { var guard = await RequirePortalAdminAsync(ct); if (guard is not null) return guard; SetTitles(id.HasValue ? "Edit module" : "Create module"); if (id.HasValue) { var row = await _repo.GetModuleAsync(id.Value, ct); if (row is null) return NotFound(); Input = new InputModel { ModuleId = row.ModuleId, ModuleKey = row.ModuleKey, DisplayName = row.DisplayName, ModuleType = row.ModuleType, SchemaName = row.SchemaName, Description = row.Description, IsEnabled = row.IsEnabled, SortOrder = row.SortOrder }; } else { Input.ModuleType = "WebAppModule"; Input.IsEnabled = true; } return Page(); }
    public async Task<IActionResult> OnPost(CancellationToken ct) { var guard = await RequirePortalAdminAsync(ct); if (guard is not null) return guard; SetTitles(IsCreate ? "Create module" : "Edit module"); ValidateInput(); if (!ModelState.IsValid) return Page(); try { var id = await _repo.SaveModuleAsync(new ModuleEditData { ModuleId = Input.ModuleId, ModuleKey = Input.ModuleKey.Trim(), DisplayName = Input.DisplayName.Trim(), ModuleType = Input.ModuleType, SchemaName = Input.SchemaName.Trim(), Description = Clean(Input.Description), IsEnabled = Input.IsEnabled, SortOrder = Input.SortOrder }, ct); StatusMessage = IsCreate ? "Module created." : "Module updated."; return RedirectToPage("~/admin/moduleedit", new { id }); } catch (SqlException ex) { ModelState.AddModelError(string.Empty, ToFriendlySqlMessage(ex, "The module could not be saved.")); return Page(); } }
    public async Task<IActionResult> OnPostDelete(CancellationToken ct) { var guard = await RequirePortalAdminAsync(ct); if (guard is not null) return guard; try { await _repo.DeleteModuleAsync(Input.ModuleId, ct); StatusMessage = "Module deleted."; return RedirectToPage("~/admin/modules"); } catch (SqlException ex) { ModelState.AddModelError(string.Empty, ToFriendlySqlMessage(ex, "The module could not be deleted.")); return Page(); } }
    private void ValidateInput() { if (!KeyPattern.IsMatch(Input.ModuleKey ?? string.Empty)) ModelState.AddModelError(nameof(Input.ModuleKey), "Use a stable key with letters, digits, dash, underscore or dot."); if (!SchemaPattern.IsMatch(Input.SchemaName ?? string.Empty)) ModelState.AddModelError(nameof(Input.SchemaName), "Schema names must start with a letter and then use letters, digits or underscore."); if (string.IsNullOrWhiteSpace(Input.ModuleType)) ModelState.AddModelError(nameof(Input.ModuleType), "Select a module type."); }
    private static OptionItem Opt(string value, string label) => new() { Value = value, Label = label }; private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim(); private static string ToFriendlySqlMessage(SqlException ex, string fallback) => ex.Number is 2601 or 2627 ? "A module with the same key already exists." : ex.Number == 547 ? "Delete or update dependent rows first." : fallback;
    public sealed class InputModel { public int ModuleId { get; set; } [Required, StringLength(100), Display(Name = "Module key")] public string ModuleKey { get; set; } = string.Empty; [Required, StringLength(200), Display(Name = "Display name")] public string DisplayName { get; set; } = string.Empty; [Required, StringLength(50), Display(Name = "Module type")] public string ModuleType { get; set; } = string.Empty; [Required, StringLength(128), Display(Name = "Schema name")] public string SchemaName { get; set; } = string.Empty; [StringLength(500)] public string? Description { get; set; } [Display(Name = "Enabled")] public bool IsEnabled { get; set; } = true; [Display(Name = "Sort order")] public int SortOrder { get; set; } }
}
