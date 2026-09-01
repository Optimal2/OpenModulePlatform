// File: OpenModulePlatform.Auth/wwwroot/js/login.js
// The login page's dialog wiring. Extracted from an inline <script> block in
// Pages/Login.cshtml so the Auth app runs under the strict
// Content-Security-Policy (no script-src 'unsafe-inline') — see
// docs/CONTENT_SECURITY_POLICY.md.
(function () {
    "use strict";

    var openDialog = function (dialog) {
        if (!dialog) {
            return;
        }

        if (typeof dialog.showModal === "function") {
            dialog.showModal();
            return;
        }

        dialog.setAttribute("open", "");
    };

    document.querySelectorAll("[data-dialog-target]").forEach(function (button) {
        button.addEventListener("click", function () {
            openDialog(document.getElementById(button.dataset.dialogTarget));
        });
    });

    document.querySelectorAll("dialog").forEach(function (dialog) {
        if (dialog.dataset.openOnLoad === "true") {
            openDialog(dialog);
        }

        dialog.querySelectorAll("[data-dialog-cancel]").forEach(function (button) {
            button.addEventListener("click", function () {
                dialog.close();
            });
        });
    });

    document.querySelectorAll("details.auth-options").forEach(function (details) {
        var summary = details.querySelector("summary[aria-expanded]");
        var updateExpanded = function () {
            if (summary) {
                summary.setAttribute("aria-expanded", details.open ? "true" : "false");
            }
        };

        updateExpanded();
        details.addEventListener("toggle", updateExpanded);
    });
}());
