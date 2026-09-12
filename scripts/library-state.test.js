const test = require('node:test');
const assert = require('node:assert/strict');
const {createIndex, findSeries} = require('../src/Cruncharr.API/wwwroot/js/library-state.js');

test('year-qualified editions need a verified provider identity when the unsuffixed name is ambiguous', () => {
    const original = {sonarrSeriesId:1,title:'Witchblade'};
    const anime = {sonarrSeriesId:2,title:'Witchblade (2006)',crunchyrollSeriesIds:['CR-ANIME']};
    const index = createIndex([original,anime]);
    assert.equal(findSeries(index,{title:'Witchblade'}),null);
    assert.equal(findSeries(index,{id:'CR-ANIME',title:'Witchblade'}),anime);
    assert.equal(findSeries(index,{title:'Witchblade (2006)'}),anime);
});

test('library badges match titles and aliases for shows that are not in Cruncharr history', () => {
    const series = {sonarrSeriesId: 1, title:'Attack on Titan', titles:['Shingeki no Kyojin'], episodeFileCount:90};
    const index = createIndex([series]);
    assert.equal(findSeries(index, {id:'NEW',title:'Attack on Titan'}), series);
    assert.equal(findSeries(index, {title:'Shingeki no Kyojin'}), series);
    assert.equal(findSeries(index, {title:'Attack on Titan: Junior High'}), null);
});

test('library matching tolerates display punctuation without assigning ambiguous titles', () => {
    const first = {sonarrSeriesId:1,title:'Wistoria: Wand and Sword',titles:['Shared title'],crunchyrollSeriesIds:['CR1']};
    const second = {sonarrSeriesId:2,title:'Other show',titles:['Shared title']};
    const index = createIndex([first,second]);
    assert.equal(findSeries(index,{title:'WISTORIA - Wand and Sword'}), first);
    assert.equal(findSeries(index,{title:'Shared title'}), null);
    assert.equal(findSeries(index,{id:'CR1',title:'Shared title'}), first);
    assert.equal(findSeries(index,{title:''}), null);
});
