// Ensures the page's feedback block ([data-omp-feedback]) is visible after
// load. Contextual placement means the block can sit deep in the page, and
// page-local scroll-restore scripts can move the viewport away from it -
// so the check runs on window load, after those scripts, and only scrolls
// when the block is actually outside the viewport.
(function () {
    'use strict';

    // R8-P5-10..15: a page may render the feedback block more than once -- typically a
    // status message near the top and a validation summary next to the form far below.
    // querySelector took the first one, so on the pages that do this the operator was
    // scrolled to a success notice while the error they needed to read stayed off-screen.
    // Prefer whichever block actually carries an error; fall back to the first.
    var pick = function () {
        var blocks = document.querySelectorAll('[data-omp-feedback]');
        if (blocks.length === 0) {
            return null;
        }

        for (var i = 0; i < blocks.length; i++) {
            if (blocks[i].querySelector('[role="alert"]')) {
                return blocks[i];
            }
        }

        return blocks[0];
    };

    var reveal = function () {
        var feedback = pick();
        if (!feedback) {
            return;
        }

        var rect = feedback.getBoundingClientRect();
        if (rect.top < 0 || rect.bottom > window.innerHeight) {
            feedback.scrollIntoView({ block: 'center' });
        }

        // Focus without re-scrolling so assistive tech starts at the
        // message; role=alert/status handles the announcement itself.
        if (typeof feedback.focus === 'function') {
            feedback.focus({ preventScroll: true });
        }
    };

    if (document.readyState === 'complete') {
        reveal();
    } else {
        window.addEventListener('load', reveal);
    }
})();
