// Ensures the page's feedback block ([data-omp-feedback]) is visible after
// load. Contextual placement means the block can sit deep in the page, and
// page-local scroll-restore scripts can move the viewport away from it -
// so the check runs on window load, after those scripts, and only scrolls
// when the block is actually outside the viewport.
(function () {
    'use strict';

    var reveal = function () {
        var feedback = document.querySelector('[data-omp-feedback]');
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
