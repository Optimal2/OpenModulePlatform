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

            const inheritedBackground = el => {
                for (let node = el; node; node = node.parentElement) {
                    const bg = getComputedStyle(node).backgroundColor;
                    if (bg && bg !== 'transparent' && !bg.startsWith('rgba(0, 0, 0, 0)')) {
                        return bg;
                    }
                }
                return 'rgb(255, 255, 255)';
            };

            const ownText = el => [...el.childNodes].filter(
                n => n.nodeType === Node.TEXT_NODE && n.textContent.trim().length > 0);
            const opaqueBackground = node => {
                const style = getComputedStyle(node);
                // Computed opaque sRGB colors use rgb(), translucent ones rgba().
                // Images and translucent/composited surfaces need a different model.
                if (!style.backgroundColor.startsWith('rgb(') || style.backgroundImage !== 'none') { return null; }
                for (let ancestor = node; ancestor; ancestor = ancestor.parentElement) {
                    const s = getComputedStyle(ancestor);
                    if (Number(s.opacity) !== 1 || s.filter !== 'none' || s.mixBlendMode !== 'normal') { return null; }
                }
                return style.backgroundColor;
            };
            const effectiveBackground = el => {
                const fallback = inheritedBackground(el);
                let surface = null;
                for (const text of ownText(el)) {
                    const range = document.createRange();
                    range.selectNodeContents(text);
                    for (const rect of range.getClientRects()) {
                        if (rect.width <= 0 || rect.height <= 0) { continue; }
                        // Require coverage of every text fragment, not just the control's center.
                        const dx = Math.min(0.5, rect.width / 4);
                        const dy = Math.min(0.5, rect.height / 4);
                        const points = [
                            [rect.left + dx, rect.top + dy], [rect.right - dx, rect.top + dy],
                            [rect.left + dx, rect.bottom - dy], [rect.right - dx, rect.bottom - dy],
                            [(rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2]
                        ];
                        for (const [x, y] of points) {
                            // The browser supplies paint order and honors clipping/transforms.
                            const stack = document.elementsFromPoint(x, y);
                            const textIndex = stack.indexOf(el);
                            if (textIndex < 0) { return fallback; }
                            const behind = stack.slice(textIndex).find(node => opaqueBackground(node));
                            if (!behind || (surface && behind !== surface)) { return fallback; }
                            const bounds = behind.getBoundingClientRect();
                            if (bounds.left > rect.left || bounds.right < rect.right ||
                                bounds.top > rect.top || bounds.bottom < rect.bottom) { return fallback; }
                            surface = behind;
                        }
                    }
                }
                return surface ? opaqueBackground(surface) : fallback;
            };

            // Decorative fills often opt out of hit testing. Include them temporarily
            // without changing their geometry/paint, and restore the exact inline styles.
            const hitTestOverrides = [...document.querySelectorAll('body, body *')]
                .filter(el => getComputedStyle(el).pointerEvents === 'none')
                .map(el => [el, el.getAttribute('style')]);
            try {
                for (const [el] of hitTestOverrides) { el.style.setProperty('pointer-events', 'auto', 'important'); }
                for (const el of document.querySelectorAll('body *')) {
                    if (!el.checkVisibility || !el.checkVisibility()) { continue; }
                    if (ownText(el).length === 0) { continue; }
                    const style = getComputedStyle(el);
                    if (style.color === effectiveBackground(el)) {
                        findings.push(`invisible text (color equals background ${style.color}): ${describe(el)} "${el.textContent.trim().slice(0, 40)}"`);
                    }
                }
            }
            finally {
                for (const [el, originalStyle] of hitTestOverrides) {
                    if (originalStyle === null) { el.removeAttribute('style'); }
                    else { el.setAttribute('style', originalStyle); }
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
