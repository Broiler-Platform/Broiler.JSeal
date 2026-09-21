// Fixed J00 script-goal fixtures for the two current-source CLI hosts. Not a Test262 suite.
// Expected results describe the target behavior, never the known-bad VM baseline.
var j00Count = 0;
function probe(id, expected, body) {
    var actual;
    try { actual = String(body()); }
    catch (error) { actual = 'throw:' + error.name; }
    print('J00\t' + JSON.stringify({
        id: id, expected: expected, actual: actual,
        outcome: actual === expected ? 'matches-target' : 'differs-from-target'
    }));
    j00Count++;
}

probe('V07.array-species', 'true', function () {
    class Derived extends Array {}
    return new Derived(1, 2).map(function (x) { return x; }) instanceof Derived;
});
probe('V09.concat-spreadable', 'a', function () {
    var value = {0: 'a', length: 1};
    value[Symbol.isConcatSpreadable] = true;
    return [].concat(value).join(',');
});
probe('V04.regexp-guard', 'throw:TypeError', function () {
    return 'abc'.startsWith(/a/);
});
probe('V01.numeric-coercion-order', 'ab', function () {
    var order = '';
    var a = {valueOf: function () { order += 'a'; return 1; }};
    var b = {valueOf: function () { order += 'b'; return 2; }};
    a - b;
    return order;
});
probe('V02.frozen-symbol', '0', function () {
    var target = Object.freeze({}), key = Symbol();
    try { target[key] = 1; } catch (error) {}
    return Object.getOwnPropertySymbols(target).length;
});
probe('V05.mapped-arguments', '7', function () {
    function f(x) { arguments[0] = 7; return x; }
    return f(1);
});
probe('V14.direct-eval', '7', function () {
    var local = 7;
    return eval('local');
});
probe('F08.normalization', 'true', function () {
    return 'e\u0301'.normalize('NFC') === '\u00e9';
});
probe('F09.unicode-property-escape', 'true', function () {
    return new RegExp('\\p{Letter}', 'u').test('a');
});
probe('F06.resizable-buffer', 'true|16|function', function () {
    var buffer = new ArrayBuffer(8, {maxByteLength: 16});
    return [buffer.resizable, buffer.maxByteLength, typeof buffer.resize].join('|');
});

// Availability probes deliberately do not claim that the entire API behaves correctly.
[
    ['BigInt', 'function'], ['BigInt64Array', 'function'], ['BigUint64Array', 'function'],
    ['SharedArrayBuffer', 'function'], ['Atomics', 'object'], ['Intl', 'object'],
    ['Iterator', 'function'], ['DisposableStack', 'function'], ['AsyncDisposableStack', 'function'],
    ['SuppressedError', 'function'], ['Float16Array', 'function'], ['ShadowRealm', 'function'],
    ['WeakRef', 'function'], ['FinalizationRegistry', 'function']
].forEach(function (entry) {
    probe('global.' + entry[0], entry[1], function () { return typeof globalThis[entry[0]]; });
});
[
    ['Array.fromAsync', function () { return typeof Array.fromAsync; }],
    ['Object.groupBy', function () { return typeof Object.groupBy; }],
    ['Map.groupBy', function () { return typeof Map.groupBy; }],
    ['Promise.withResolvers', function () { return typeof Promise.withResolvers; }],
    ['Promise.try', function () { return typeof Promise.try; }],
    ['RegExp.escape', function () { return typeof RegExp.escape; }],
    ['Math.f16round', function () { return typeof Math.f16round; }],
    ['Set.prototype.union', function () { return typeof Set.prototype.union; }],
    ['ArrayBuffer.prototype.transfer', function () { return typeof ArrayBuffer.prototype.transfer; }],
    ['DataView.prototype.getFloat16', function () { return typeof DataView.prototype.getFloat16; }],
    ['JSON.rawJSON', function () { return typeof JSON.rawJSON; }],
    ['JSON.isRawJSON', function () { return typeof JSON.isRawJSON; }],
    ['Array.prototype.toLocaleString', function () { return typeof Array.prototype.toLocaleString; }]
].forEach(function (entry) { probe('method.' + entry[0], 'function', entry[1]); });
probe('symbol.dispose', 'symbol', function () { return typeof Symbol.dispose; });
probe('symbol.asyncDispose', 'symbol', function () { return typeof Symbol.asyncDispose; });
print('J00-DONE\t' + j00Count);
