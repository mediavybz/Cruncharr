(function (root, factory) {
    const api = factory();
    if (typeof module === 'object' && module.exports) module.exports = api;
    else root.CruncharrLibrary = api;
})(typeof window !== 'undefined' ? window : this, function () {
    const normalizeTitle = title => String(title || '').replace(/&/g, 'and').normalize('NFKD').toLowerCase().replace(/\p{M}/gu, '').replace(/[^\p{L}\p{N}]/gu, '');

    function createIndex(series) {
        const ids = new Map();
        const titles = new Map();
        const add = (map, key, item) => {
            if (!key) return;
            if (!map.has(key)) map.set(key, item);
            else if (map.get(key)?.sonarrSeriesId !== item.sonarrSeriesId) map.set(key, null);
        };
        for (const item of series || []) {
            for (const id of item.crunchyrollSeriesIds || []) add(ids, id, item);
            for (const title of [item.title, ...(item.titles || [])]) add(titles, normalizeTitle(title), item);
        }
        return { ids, titles };
    }

    function findSeries(index, series) {
        if (!index) return null;
        const id = series.id || series.seriesId;
        if (index.ids.has(id)) return index.ids.get(id);
        return index.titles.get(normalizeTitle(series.title || series.seriesTitle)) || null;
    }
    return { createIndex, findSeries };
});
