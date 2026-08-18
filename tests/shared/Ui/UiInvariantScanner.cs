using Microsoft.Playwright;

namespace OpenModulePlatform.TestSupport.Ui;

/// <summary>
/// Generic "broken UI" checks that hold for every page: no horizontal
/// overflow, no text rendered in the same color as its effective background,
/// no zero-size clickable elements. These are invariants — they need no
/// per-page expectations, so adding a page to the scan list costs nothing.
/// </summary>
public static class UiInvariantScanner
{
    private const string Script =
        """
        () => {
            const findings = [];
            const describe = el => {
                let name = el.tagName.toLowerCase();
                if (el.id) { name += '#' + el.id; }
                else if (el.classList.length) { name += '.' + [...el.classList].slice(0, 3).join('.'); }
                return name;
            };

            const root = document.documentElement;
            if (root.scrollWidth > root.clientWidth + 1) {
                const offenders = [];
                for (const el of document.querySelectorAll('body *')) {
                    const r = el.getBoundingClientRect();
                    if (r.width > 0 && r.right > root.clientWidth + 1 && offenders.length < 5) {
                        offenders.push(describe(el));
                    }
                }
                findings.push(`horizontal overflow: scrollWidth ${root.scrollWidth} > viewport ${root.clientWidth}; sticks out: ${offenders.join(', ')}`);
            }

            const effectiveBackground = el => {
                for (let node = el; node; node = node.parentElement) {
                    const bg = getComputedStyle(node).backgroundColor;
                    if (bg && bg !== 'transparent' && !bg.startsWith('rgba(0, 0, 0, 0)')) {
                        return bg;
                    }
                }
                return 'rgb(255, 255, 255)';
            };

            for (const el of document.querySelectorAll('body *')) {
                if (!el.checkVisibility || !el.checkVisibility()) { continue; }
                const hasOwnText = [...el.childNodes].some(
                    n => n.nodeType === Node.TEXT_NODE && n.textContent.trim().length > 0);
                if (!hasOwnText) { continue; }
                const style = getComputedStyle(el);
                if (style.color === effectiveBackground(el)) {
                    findings.push(`invisible text (color equals background ${style.color}): ${describe(el)} "${el.textContent.trim().slice(0, 40)}"`);
                }
            }

            for (const el of document.querySelectorAll('a[href], button:not([disabled])')) {
                if (el.offsetParent === null) { continue; }
                const r = el.getBoundingClientRect();
                if (r.width < 2 || r.height < 2) {
                    findings.push(`zero-size clickable: ${describe(el)}`);
                }
            }

            return findings;
        }
        """;

    public static async Task<IReadOnlyList<string>> ScanAsync(IPage page)
        => await page.EvaluateAsync<string[]>(Script) ?? [];
}
