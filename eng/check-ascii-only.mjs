// The emitted asset is ASCII only, and the escaping that makes it so changes no program's meaning.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { asciiOnly } from "./steam-ui-fragments.mjs";

// Each source evaluates to a value; escaped, it must evaluate to the same one.
const cases = [
    String.raw`"a … b"`,
    String.raw`"identity escape \— kept"`,
    String.raw`"escaped backslash \\— then a dash"`,
    String.raw`"three \\\— backslashes"`,
    String.raw`"astral 😀 and escaped \😀"`,
    "`template ${1 + 1} · ×`",
    String.raw`/^[—–]+$/u.test("—–") + "|" + /^[—–]\—$/.test("——") + "|" + /\—/.test("—")`,
];
for (const source of cases) {
    const escaped = asciiOnly(source);
    assert.ok(!/[^\x00-\x7f]/u.test(escaped), `not ASCII: ${escaped}`);
    const value = (text) => new Function(`return (${text});`)();
    assert.equal(value(escaped), value(source), `meaning changed: ${source} -> ${escaped}`);
}
assert.equal(asciiOnly("plain ascii \\n"), "plain ascii \\n", "ASCII text is left as it is");
assert.equal(asciiOnly("// comment …\n"), "// comment \\u2026\n", "a comment is escaped too");

const asset = process.argv[2];
if (asset) {
    const text = readFileSync(asset, "utf8");
    const first = text.search(/[^\x00-\x7f]/u);
    assert.equal(first, -1, `the asset has a non-ASCII character at ${first}`);
}
console.log(
    "ASCII asset: escapes keep identity escapes, escaped backslashes, astral pairs, templates and regular expressions; the asset is ASCII only.",
);
