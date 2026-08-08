(function (root, factory) {
    const api = factory();
    if (typeof module === 'object' && module.exports) module.exports = api;
    if (root) root.CruncharrCalendarRequests = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
    'use strict';

    function normalizeDubFilter(value, options) {
        const requested = String(value || 'none').trim().toLowerCase();
        if (!requested || requested === 'none') return 'none';

        const match = (options || []).find(option =>
            String(option?.value || '').toLowerCase() === requested);
        return match?.value || 'none';
    }

    function createLatestRequestGate(createAbortController) {
        const makeController = createAbortController || (() => new AbortController());
        let generation = 0;
        let activeController = null;

        return {
            begin() {
                generation++;
                activeController?.abort();
                activeController = makeController();
                return {
                    generation,
                    controller: activeController,
                    signal: activeController.signal
                };
            },

            isCurrent(request) {
                return !!request &&
                    request.generation === generation &&
                    request.controller === activeController &&
                    !request.signal.aborted;
            },

            finish(request) {
                if (request?.controller === activeController) activeController = null;
            },

            cancel() {
                generation++;
                activeController?.abort();
                activeController = null;
            }
        };
    }

    return { normalizeDubFilter, createLatestRequestGate };
});
