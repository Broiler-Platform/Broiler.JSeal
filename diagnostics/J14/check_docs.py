"""Check local Markdown links/anchors and example project paths; no network access."""
import json
import re
import shutil
import subprocess
from pathlib import Path
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[2]


def markdown_files():
    """Markdown files that are not ignored, as ripgrep lists them; git ls-files when rg is absent."""
    if shutil.which("rg"):
        listed = subprocess.check_output(["rg", "--files", "-g", "*.md"], cwd=ROOT, text=True).splitlines()
    else:
        # Tracked plus untracked-but-not-ignored, which is what rg honours from .gitignore. rg also
        # skips hidden paths by default, so they are dropped here to check the same set.
        listed = [p for p in subprocess.check_output(
            ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "*.md"],
            cwd=ROOT, text=True).split("\0")
            if p and not any(part.startswith(".") for part in Path(p).parts) and (ROOT / p).is_file()]
    return sorted({Path(p).as_posix() for p in listed})


files = [ROOT / p for p in markdown_files()]
errors = []
checked = 0


def headings(path):
    counts, result = {}, set()
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        match = re.match(r"^#{1,6}\s+(.+?)\s*#*\s*$", line)
        if not match:
            continue
        slug = re.sub(r"[^\w\- ]", "", match[1].lower()).replace(" ", "-")
        count = counts.get(slug, 0)
        counts[slug] = count + 1
        result.add(f"{slug}-{count}" if count else slug)
    return result


for file in files:
    source = file.read_text(encoding="utf-8-sig")
    prose = re.sub(r"^```.*?^```\s*$", "", source, flags=re.M | re.S)
    # This repository uses inline links. Also accept simple reference-link definitions.
    targets = re.findall(r"\]\((?:<([^>]+)>|([^\s)]+))(?:\s+\"[^\"]*\")?\)", prose)
    targets += [(target, "") for target in re.findall(r"^\s*\[[^]]+\]:\s*<?([^\s>]+)>?", prose, re.M)]
    for angle, plain in targets:
        target = angle or plain
        parsed = urlsplit(target)
        if parsed.scheme or parsed.netloc:
            continue
        checked += 1
        path = (file.parent / unquote(parsed.path)).resolve() if parsed.path else file
        if not path.is_relative_to(ROOT):
            errors.append(f"{file.relative_to(ROOT)}: link leaves repository: {target}")
        elif not path.exists():
            errors.append(f"{file.relative_to(ROOT)}: missing target: {target}")
        elif parsed.fragment and path.suffix == ".md" and unquote(parsed.fragment) not in headings(path):
            errors.append(f"{file.relative_to(ROOT)}: missing heading: {target}")
    # Fences explicitly marked external document optional sibling checkout commands.
    local_examples = re.sub(r"^```[^\n]*\bexternal\b[^\n]*\n.*?^```\s*$", "", source, flags=re.M | re.S)
    for target in re.findall(r"(?:--project\s+|dotnet\s+(?:build|test|pack)\s+)([\w./-]+\.(?:csproj|slnx))", local_examples):
        checked += 1
        if not (ROOT / target).exists():
            errors.append(f"{file.relative_to(ROOT)}: missing example project: {target}")

print(json.dumps({"markdownFiles": len(files), "localTargetsChecked": checked, "errors": errors}, indent=2))
raise SystemExit(bool(errors))
