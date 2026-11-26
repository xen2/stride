#!/usr/bin/env python3
"""Coverage report of a CI run, for coverage-report.yml.

Reads the coverage-<lane> artifacts of the run (one Cobertura per lane, see .github/actions/coverage-lane) and,
for a PR, the run's diff and the artifacts of the base commit's master run (for a push, of its parent's run). Writes into --out:
  result.json  what to post: {"kind": "none" | "code" | "tests" | "push", "comment": markdown,
               "check": {"title", "summary", "text", "annotations"}}
  comment.md   the comment of the PR, as in result.json
  summary.md   the step summary of the report job: the comment, the view, and the coverage of every assembly and of
               each lane
  view.md      the view of a PR: the code around its changed lines that no test runs and around the lines that lost
               their coverage, as diff blocks whose marks are the coverage. The check run text and the step summary
               have size limits and cut it; this file has all of it.
With --not-updated, writes only comment.md: the PR's comment of an earlier push (--previous), with a note on top that
this push got no report.
The artifacts and the tested commit come from PR code, so everything read from them is data: assembly names, paths
and lane names are checked before they reach a comment, and code is shown only in fenced blocks. The view reads the
code of the tested commit (--source-commit) with git in the current folder. Standard library only, so it runs on any
runner and locally:
  python3 .github/scripts/coverage-report.py --current <dir> [--baseline <dir>] [--diff <file>] [--source-commit <sha>] --out <dir>
  python3 .github/scripts/coverage-report.py --not-updated <reason> --previous <file> --head <sha> --out <dir>
"""

import argparse
import colorsys
import json
import os
import re
import subprocess
import xml.etree.ElementTree as ET
from collections import defaultdict

# Artifact coverage-<key> -> name in the report, in the order shown. All on Windows.
LANES = {
    "game-Direct3D11": "D3D11",
    "game-api-neutral": "ApiNeutral",
    "simple": "Simple",
    "game-Direct3D12": "D3D12",
    "game-Vulkan": "Vulkan",
    "editor-wpf": "WPF editor",
    "editor-avalonia": "Avalonia editor",
    "samples": "Samples",
    "editor-screenshots": "Game Studio screenshots",
}
# Lanes only pushes run (main.yml): a PR that doesn't run them isn't missing them
PUSH_LANES = {"samples", "editor-screenshots"}
NOT_MEASURED = "Linux, macOS, iOS, Android"
# An assembly the PR doesn't change is listed when its covered lines change by more than this: two runs of the same
# commit differ by up to 32 lines in one assembly (timing).
NOISE = 50
MAX_ASSEMBLIES = 12
# 50 per API call; the check run gets them in several calls
MAX_ANNOTATIONS = 200
# GitHub's limits: 65535 characters for the text of a check run, 1 MiB for a step summary (with a margin)
MAX_CHECK_TEXT = 65000
MAX_SUMMARY_BYTES = 1000000
# Lines of code shown around each line of the view
CONTEXT = 2
VIEW_LEGEND = ("`+` covered · `-` not covered · `!` covered at the base commit, not now · `new` the PR adds or changes "
               "the line · no mark: no code, or an unchanged line that didn't lose its coverage")
MARKER = "<!-- coverage-report -->"
# The note on top of a comment that a push got no report, up to the blank line that ends it
NOT_UPDATED_MARKER = "<!-- coverage-not-updated -->"
NOT_UPDATED = re.compile(re.escape(NOT_UPDATED_MARKER) + r"\n(?:>.*\n)*\n")

ASSEMBLY_NAME = re.compile(r"^[A-Za-z_][\w.]*$")
# The workspace of the runner that wrote the paths: D:\a\stride\stride\, /home/runner/work/stride/stride/
WORKSPACE = re.compile(
    r"^(?:[A-Za-z]:[\\/]a[\\/][^\\/]+[\\/][^\\/]+|/home/runner/work/[^/]+/[^/]+|/Users/runner/work/[^/]+/[^/]+)[\\/]")
# Projects whose code isn't measured (as in tests/coverage.runsettings)
TEST_PROJECT = re.compile(
    r"Test|^Stride\.Graphics\.Regression$|AutoTesting$|^Stride\.XunitRunner$|^Stride\.CompareGold$|"
    r"^Stride\.ScreenshotComparator")
# Sources left out of the coverage (as in tests/coverage.runsettings)
NOT_COVERED_SOURCE = re.compile(r"(^|/)obj/|\.gen\.cs$|Bytecodes/[^/]*\.Level_\w+\.cs$")


def repo_path(filename):
    """Repo-relative path with forward slashes, or None for a path outside the workspace."""
    path = WORKSPACE.sub("", filename, count=1)
    if path == filename and (filename.startswith("/") or re.match(r"^[A-Za-z]:", filename)):
        return None
    path = path.replace("\\", "/")
    if ".." in path.split("/"):
        return None
    return path


class Lane:
    """Coverage of one lane: per (assembly, file), line -> covered, the effect cache files it restored, and whether
    its job had failed by then (None when the artifact doesn't say)."""

    def __init__(self, path):
        self.files = defaultdict(dict)
        self.restored_effects = restored_effects(os.path.dirname(path))
        self.failed = job_failed(os.path.dirname(path))
        assembly = None
        for event, element in ET.iterparse(path, events=("start", "end")):
            if event == "start" and element.tag == "package":
                name = element.get("name") or ""
                assembly = name if ASSEMBLY_NAME.match(name) else None
            elif event == "end" and element.tag == "class":
                file = repo_path(element.get("filename") or "")
                if assembly and file:
                    lines = self.files[(assembly, file)]
                    for line in element.iter("line"):
                        number = int(line.get("number", "0"))
                        lines[number] = lines.get(number, False) or int(line.get("hits", "0")) > 0
                element.clear()
            elif event == "end" and element.tag == "package":
                element.clear()


def restored_effects(directory):
    """The effect cache files the lane restored (effect-cache.txt), or None when it has no effect cache."""
    try:
        with open(os.path.join(directory, "effect-cache.txt"), encoding="utf-8-sig") as file:
            text = file.read().strip()
    except OSError:
        return None
    return int(text) if re.fullmatch(r"[0-9]{1,9}", text) else None


def job_failed(directory):
    """Whether the lane's job had failed before its coverage step (job-status.txt), or None when it doesn't say."""
    try:
        with open(os.path.join(directory, "job-status.txt"), encoding="utf-8-sig") as file:
            status = file.read().strip()
    except OSError:
        return None
    return {"success": False, "failure": True}.get(status)


def load(directory):
    """Lanes of a downloaded run: {lane key: Lane}, from <directory>/coverage-<key>/Cobertura.xml."""
    lanes = {}
    if directory and os.path.isdir(directory):
        for key in LANES:
            path = os.path.join(directory, f"coverage-{key}", "Cobertura.xml")
            if os.path.isfile(path):
                lanes[key] = Lane(path)
    return lanes


def merge(lanes):
    """Union of the lanes: per (assembly, file), line -> covered in any lane."""
    merged = defaultdict(dict)
    for lane in lanes:
        for key, lines in lane.files.items():
            target = merged[key]
            for number, covered in lines.items():
                target[number] = target.get(number, False) or covered
    return merged


def per_file(merged):
    """Per file: line -> covered in any assembly that compiles it, and those assemblies."""
    lines_of = defaultdict(dict)
    assemblies_of = defaultdict(set)
    for (assembly, file), lines in merged.items():
        assemblies_of[file].add(assembly)
        target = lines_of[file]
        for number, covered in lines.items():
            target[number] = target.get(number, False) or covered
    return lines_of, assemblies_of


def totals(merged):
    """Per assembly: [covered, coverable]."""
    result = defaultdict(lambda: [0, 0])
    for (assembly, _), lines in merged.items():
        result[assembly][0] += sum(lines.values())
        result[assembly][1] += len(lines)
    return result


def percent(covered, coverable):
    return f"{100 * covered / coverable:.1f}%" if coverable else "n/a"


def paint(text, hue):
    """Text in a color of GitHub's math: hue 0 is red, 120 green, dark enough for the light and dark themes. In the
    $`...`$ form, Markdown leaves the backslashes to the math (with $...$, it turns \\% into %, a LaTeX comment). Math
    drops plain spaces, so they become \\ (a backslash and a space)."""
    r, g, b = (round(255 * c) for c in colorsys.hls_to_rgb(hue / 360, 0.42, 0.75))
    text = text.replace("%", "\\%").replace(" ", "\\ ")
    return f"$`\\color{{#{r:02x}{g:02x}{b:02x}}}{{\\mathsf{{{text}}}}}`$"


def painted_percent(covered, coverable):
    """The coverage, from red at 0% to green at 100%."""
    return paint(percent(covered, coverable), 120 * covered / coverable) if coverable else "n/a"


def plural(count, word):
    return f"{count} {word}{'s' if count != 1 else ''}"


def signed_points(points):
    return f"{points:+.1f}" if round(points, 1) != 0 else "0.0"


def signed_lines(lines):
    return f"{lines:+d}" if lines else "0"


def painted_change(text, covered_change):
    """A change, green or red when its covered lines change by more than the noise of two runs."""
    return paint(text, 120 if covered_change > 0 else 0) if abs(covered_change) > NOISE else text


def change_text(covered_change, points):
    """A change in points and covered lines, painted."""
    return painted_change(f"{signed_points(points)} points, {signed_lines(covered_change)} "
                          f"line{'s' if abs(covered_change) != 1 else ''} covered", covered_change)


# The painted text of the comment, plain for the check run, whose rendering of math isn't known
PAINTED = re.compile(r"\$`\\color\{#\w+\}\{\\mathsf\{(.*?)\}\}`\$")


def plain(text):
    return PAINTED.sub(lambda m: m[1].replace("\\%", "%").replace("\\ ", " "), text)


class FileDiff:
    """What a diff does to one file: its path at the base (None for a new file), the lines it adds or changes, and
    the base line of each line it keeps."""

    def __init__(self, old):
        self.old = old
        self.added = set()
        # Lines inside a hunk that the diff keeps: head line -> base line
        self.kept = {}
        # After each hunk: (its first head line after it, head lines minus base lines up to there)
        self.shifts = []

    def base_line(self, number):
        """The base line of a head line, or None for a line the diff adds or changes."""
        if number in self.added:
            return None
        if number in self.kept:
            return self.kept[number]
        shift = 0
        for after, total in self.shifts:
            if after > number:
                break
            shift = total
        return number - shift


def diff_path(text, prefix):
    name = text.strip().strip('"')
    return name[len(prefix):] if name.startswith(prefix) else None


def read_diff(path):
    """Per head file path: its FileDiff."""
    files = {}
    file = old = None
    in_hunk = False
    head = base = shift = 0
    with open(path, encoding="utf-8", errors="replace") as diff:
        for line in diff:
            line = line.rstrip("\n")
            if line.startswith("diff --git "):
                file = old = None
                in_hunk = False
                shift = 0
            elif not in_hunk and line.startswith("rename from "):
                old = line[len("rename from "):].strip('"')
            elif not in_hunk and line.startswith("rename to "):
                files[line[len("rename to "):].strip('"')] = FileDiff(old)
            elif not in_hunk and line.startswith("--- "):
                old = diff_path(line[4:], "a/")
            elif not in_hunk and line.startswith("+++ "):
                name = diff_path(line[4:], "b/")
                file = files.setdefault(name, FileDiff(old)) if name else None
            elif line.startswith("@@"):
                match = re.match(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", line)
                in_hunk = match is not None and file is not None
                if in_hunk:
                    base, base_count, head, head_count = (int(g) if g is not None else 1 for g in match.groups())
                    # A hunk of no head line comes after its head line
                    shift += head_count - base_count
                    file.shifts.append((head + head_count if head_count else head + 1, shift))
            elif in_hunk:
                if line.startswith("+"):
                    file.added.add(head)
                    head += 1
                elif line.startswith("-"):
                    base += 1
                elif line.startswith(" ") or line == "":
                    file.kept[head] = base
                    head += 1
                    base += 1
    return files


def read_projects(path):
    """Project directories -> project names, from a list of repo paths (git ls-tree)."""
    projects = {}
    if path and os.path.isfile(path):
        with open(path, encoding="utf-8") as listing:
            for line in listing:
                line = line.strip()
                if line.endswith(".csproj"):
                    directory, _, name = line.rpartition("/")
                    projects[directory] = name[: -len(".csproj")]
    return projects


def project_of(file, projects):
    directory = file.rpartition("/")[0]
    while directory:
        if directory in projects:
            return projects[directory]
        directory = directory.rpartition("/")[0]
    return None


def ranges(uncovered, coverable):
    """Ranges (first, last) of uncovered changed lines. A range stops at a coverable line that is covered or that
    the PR doesn't change; lines with no code (comments, braces) don't stop it."""
    kept = set(uncovered)
    result = []
    first = last = None
    for number in coverable:
        if number in kept:
            first = number if first is None else first
            last = number
        elif first is not None:
            result.append((first, last))
            first = None
    if first is not None:
        result.append((first, last))
    return result


def lost_lines(lines_of, assemblies_of, base_lines_of, diffs, delta):
    """Per file: the lines the PR keeps that no test runs now and that a test ran at the base commit. In a file the PR
    doesn't change, only when one of its assemblies lost more covered lines than the noise."""
    losing = {a for a, (covered, _) in delta.items() if covered < -NOISE}
    result = {}
    for file, lines in lines_of.items():
        diff = diffs.get(file)
        if diff is None and not assemblies_of[file] & losing:
            continue
        base = base_lines_of.get(diff.old if diff else file)
        if not base:
            continue
        lost = set()
        for number, covered in lines.items():
            before = number if diff is None else diff.base_line(number)
            if not covered and before is not None and base.get(before):
                lost.add(number)
        if lost:
            result[file] = lost
    return result


def read_source(commit, file):
    """The lines of a file at a commit, or None. git cat-file reads the file as it is stored, with no filter; in a
    blobless fetch, it downloads it."""
    try:
        blob = subprocess.run(["git", "cat-file", "blob", f"{commit}:{file}"], capture_output=True, check=True).stdout
    except (OSError, subprocess.CalledProcessError):
        return None
    return blob.decode("utf-8-sig", errors="replace").splitlines()


def fence(lines):
    """A code fence longer than any run of backticks in the lines."""
    longest = max((len(run) for line in lines for run in re.findall(r"`+", line)), default=0)
    return "`" * max(3, longest + 1)


def file_view(file, source, lines, changed, uncovered, lost):
    """The code around the uncovered changed lines and the lost lines of a file, as a diff block whose marks are the
    coverage, under a heading."""
    last = len(source) if source is not None else max(uncovered | lost) + CONTEXT
    shown = sorted({n for anchor in uncovered | lost for n in range(anchor - CONTEXT, anchor + CONTEXT + 1) if 1 <= n <= last})
    width = len(str(shown[-1]))
    body = []
    for i, number in enumerate(shown):
        if i == 0 or number != shown[i - 1] + 1:
            body.append(f"@@ {file}, line {number} @@")
        if number in lost:
            mark = "!"
        elif number in changed and number in lines:
            mark = "+" if lines[number] else "-"
        else:
            mark = " "
        text = source[number - 1].expandtabs(4) if source is not None else ""
        body.append(f"{mark} {number:>{width}} {'new' if number in changed else '   '} │ {text}".rstrip())
    counts = []
    if uncovered:
        counts.append(f"{len(uncovered)} not covered")
    if lost:
        counts.append(f"{len(lost)} lost")
    heading = f"#### {code(file)} · {', '.join(counts)}"
    if source is None:
        heading += " (code not available)"
    marks = fence(body)
    return "\n".join([heading, "", f"{marks}diff", *body, marks, ""])


def cut(blocks, limit, size, note):
    """The blocks that fit in limit, by size(), and then note(number left out) if any is left out."""
    kept = []
    used = 0
    for i, block in enumerate(blocks):
        # Room for the note too, unless this is the last block
        reserve = 0 if i == len(blocks) - 1 else size(note(len(blocks) - i - 1)) + 2
        if used + size(block) + 1 + reserve > limit:
            return kept + [note(len(blocks) - i)]
        kept.append(block)
        used += size(block) + 1
    return kept


def code(text):
    """Text from the PR shown as inline code: no backticks or line breaks to break out of it."""
    return "`" + re.sub(r"[`\r\n]", "?", text) + "`"


def difference(now, before):
    """The change from [covered, coverable] before to now: (covered lines, points)."""
    return now[0] - before[0], (100 * now[0] / now[1] if now[1] else 0) - (100 * before[0] / before[1] if before[1] else 0)


def changes(current, baseline):
    """The lanes to compare, the lanes both runs have whose job failed in the base run, and over the compared lanes
    the change of each assembly and of all of them."""
    failed = [key for key in current if key in baseline and baseline[key].failed]
    common = [key for key in current if key in baseline and key not in failed]
    if not common:
        return common, failed, {}, None
    now = totals(merge(current[key] for key in common))
    before = totals(merge(baseline[key] for key in common))
    delta = {a: difference(now.get(a, [0, 0]), before.get(a, [0, 0])) for a in set(now) | set(before)}
    return common, failed, delta, difference(*([sum(v[i] for v in t.values()) for i in (0, 1)] for t in (now, before)))


def change_source(args, baseline, common, failed, current):
    """The line that says what the change is against, or why none is shown."""
    if not baseline:
        return f"No change shown: {args.no_baseline}" if args.no_baseline else "No change shown: no base run."
    if not common:
        reason = f"{lane_names(failed)} failed in the base run" if failed else "the two runs have no lane in common"
        return f"No change shown: {reason}."
    change = f"Change against {args.base_label}" if args.base_label else "Change against the base commit"
    if set(common) != set(current):
        change += f", over {lane_names(common)}" + ("" if failed else " (the lanes both runs have)")
    if failed:
        change += f". Not compared: {lane_names(failed)}, which failed in the base run"
    return change


def not_updated(previous, reason, head, run_url):
    """The comment of an earlier push with a note on top that this push got no report, or None when it isn't one."""
    body = previous.replace("\r\n", "\n")
    if not body.startswith(MARKER + "\n"):
        return None
    body = NOT_UPDATED.sub("", body[len(MARKER) + 1:], count=1)
    run = f" ([report run]({run_url}))" if run_url else ""
    return (f"{MARKER}\n{NOT_UPDATED_MARKER}\n> [!NOTE]\n> Not updated for `{head[:10]}`: {reason.rstrip('.')}{run}. "
            f"What follows is of an earlier commit.\n\n{body}")


def table(heading, rows, with_change, extra=None):
    """Step summary table of (name, [covered, coverable], change, extra cell) rows: coverage and covered lines, with
    columns of their change when there is a base (blank when it didn't change, n/a when the row has no base), and a
    last column when extra names it."""
    def row(name, counts, change, cell):
        covered, coverable = counts
        cells = [name, painted_percent(covered, coverable)]
        moved = change and (change[0] or round(change[1], 1) != 0)
        if with_change:
            cells.append("n/a" if change is None else painted_change(signed_points(change[1]), change[0]) if moved else "")
        cells.append(str(covered))
        if with_change:
            cells.append("n/a" if change is None else painted_change(signed_lines(change[0]), change[0]) if moved else "")
        cells.append(str(coverable))
        if extra:
            cells.append(cell)
        return "| " + " | ".join(cells) + " |"

    names = [heading, "Coverage"] + ["Change"] * with_change + ["Covered"] + ["Change"] * with_change + ["Coverable"]
    align = ["---"] + ["--:"] * (len(names) - 1)
    if extra:
        names.append(extra)
        align.append("---")
    return ["", "| " + " | ".join(names) + " |", "|" + "|".join(align) + "|"] + [row(*r) for r in rows]


def assembly_table(total, covered_all, coverable_all, delta, change_all):
    """Table of every assembly over the lanes that ran, with its change over the lanes both runs have."""
    rows = [(a, counts, delta.get(a, (0, 0)), "") for a, counts in sorted(total.items())]
    rows.append(("**All**", [covered_all, coverable_all], change_all, ""))
    return table("Assembly", rows, bool(delta))


def lane_table(current, baseline, failed):
    """Table of each lane on its own, against the same lane in the base run, with the effect cache files each run
    restored: a lane that restored none compiled every shader, which covers more of the shader compiler. The change
    of a lane that failed in the base run is shown here only."""
    def counts(lane):
        lines = [covered for lines in lane.files.values() for covered in lines.values()]
        return [sum(lines), len(lines)]

    def effects(lane):
        if lane is None or lane.restored_effects is None:
            return "?"
        return "none" if lane.restored_effects == 0 else f"{lane.restored_effects} files"

    extra = None
    if any(lane.restored_effects is not None for lane in current.values()):
        extra = "Effect cache, base → this run" if baseline else "Effect cache"
    rows = []
    for key in (key for key in LANES if key in current):
        before = baseline.get(key)
        now = counts(current[key])
        cache = ""
        if current[key].restored_effects is not None:
            cache = f"{effects(before)} → {effects(current[key])}" if baseline else effects(current[key])
        name = LANES[key]
        if key in failed:
            name += " (not in the change: failed in the base run)"
        rows.append((name, now, difference(now, counts(before)) if before else None, cache))
    return (["", "Each lane on its own: its coverable lines are those of the assemblies its tests load."]
            + table("Lane", rows, bool(baseline), extra))


def lane_names(keys):
    return ", ".join(LANES[key] for key in LANES if key in keys)


def report(args):
    current = load(args.current)
    baseline = load(args.baseline)
    merged = merge(current.values())
    total = totals(merged)
    covered_all = sum(v[0] for v in total.values())
    coverable_all = sum(v[1] for v in total.values())
    ran = lane_names(current)
    missing = [LANES[key] for key in LANES if key not in current]
    summary = []

    common, failed, delta, change_all = changes(current, baseline)

    if not args.diff:
        head = f"## Coverage: {painted_percent(covered_all, coverable_all)} ({covered_all} of {coverable_all} lines)"
        if change_all:
            head += f" · {change_text(*change_all)}"
        summary += [head, "", f"Windows lanes: {ran or 'none'}" + (f" · not run: {', '.join(missing)}" if missing else "")]
        if args.ci_label:
            summary += ["", f"From the {args.ci_label}"]
        summary += ["", change_source(args, baseline, common, failed, current)]
        summary += assembly_table(total, covered_all, coverable_all, delta, change_all)
        return {"kind": "push"}, summary + lane_table(current, baseline, failed)

    # Changed files: product code with coverage, product code no test loaded, or tests
    projects = read_projects(args.projects)
    by_file, assembly_of = per_file(merged)
    measured_assemblies = set(total)
    diffs = read_diff(args.diff)
    changed_assemblies = set()
    not_loaded = defaultdict(int)
    tests_changed = False
    changed_lines = {}
    for file, diff in sorted(diffs.items()):
        numbers = diff.added
        if not numbers or not file.endswith(".cs") or NOT_COVERED_SOURCE.search(file):
            continue
        if file in assembly_of:
            changed_assemblies |= assembly_of[file]
            changed_lines[file] = numbers
            continue
        project = project_of(file, projects)
        if project is None or not ASSEMBLY_NAME.match(project) or not project.startswith("Stride."):
            continue
        if TEST_PROJECT.search(project):
            tests_changed = True
        elif project in measured_assemblies:
            # A file with no coverable lines (declarations only), or a new file of a loaded assembly
            changed_assemblies.add(project)
        else:
            not_loaded[project] += len(numbers)
    if not changed_lines and not changed_assemblies and not not_loaded and not tests_changed:
        return {"kind": "none"}, ["No C# change: no coverage report."]

    # Changed lines: covered, or ranges not covered, per file
    covered_changed = coverable_changed = 0
    uncovered_of = {}
    annotations = []
    for file, numbers in changed_lines.items():
        lines = by_file[file]
        coverable = sorted(n for n in numbers if n in lines)
        uncovered = [n for n in coverable if not lines[n]]
        coverable_changed += len(coverable)
        covered_changed += len(coverable) - len(uncovered)
        if not uncovered:
            continue
        uncovered_of[file] = set(uncovered)
        for first, last in ranges(uncovered, sorted(lines)):
            where = f"Line {first}" if first == last else f"Lines {first}-{last}"
            annotations.append({
                "path": file, "start_line": first, "end_line": last, "annotation_level": "warning",
                "title": "Not covered by tests",
                "message": f"{where}: no test of the Windows lanes that ran ({ran}) runs them.",
            })
    annotation_count = len(annotations)
    annotations = annotations[:MAX_ANNOTATIONS]

    # Lines that lost their coverage, over the lanes both runs have
    lost_of = {}
    if common:
        base_lines_of, _ = per_file(merge(baseline[key] for key in common))
        lost_of = lost_lines(by_file, assembly_of, base_lines_of, diffs, delta)
    lost_count = sum(len(lost) for lost in lost_of.values())

    # The view: the files with the most uncovered changed lines first
    view_files = sorted(set(uncovered_of) | set(lost_of),
                        key=lambda f: (-len(uncovered_of.get(f, ())), -len(lost_of.get(f, ())), f))
    view = [file_view(f, read_source(args.source_commit, f) if args.source_commit else None, by_file[f],
                      changed_lines.get(f, set()), uncovered_of.get(f, set()), lost_of.get(f, set()))
            for f in view_files]
    view_head = ["### Changed lines not covered, and lines that lost their coverage", "", VIEW_LEGEND, ""]

    # The assemblies the PR changes, then the others whose coverage changed by more than the noise
    listed = sorted(changed_assemblies & measured_assemblies)
    others = sorted((a for a, (lines, _) in delta.items() if abs(lines) > NOISE and a not in changed_assemblies and a in total),
                    key=lambda a: -abs(delta[a][0]))
    listed += others

    def assembly_text(assembly):
        covered, coverable = total[assembly]
        text = f"{assembly} {painted_percent(covered, coverable)}"
        if assembly in delta:
            text += f" ({change_text(*delta[assembly])})"
        return text

    lines = []
    if coverable_changed:
        head = f"**Coverage of changed lines: {painted_percent(covered_changed, coverable_changed)} ({covered_changed} of {coverable_changed})**"
        if coverable_changed - covered_changed:
            head += f" · {coverable_changed - covered_changed} not covered in {plural(len(uncovered_of), 'file')} (see the warnings)"
        lines.append(head)
    elif changed_assemblies:
        lines.append("**Coverage of changed lines:** no coverable line changed")
    lost_text = f"{plural(lost_count, 'line')} lost coverage in {plural(len(lost_of), 'file')}"
    if lost_count:
        lines.append(f"{lost_text}: tests ran them at the base commit, no test runs them now")
    if listed:
        shown = [assembly_text(a) for a in listed[:MAX_ASSEMBLIES]]
        if len(listed) > MAX_ASSEMBLIES:
            shown.append(f"and {len(listed) - MAX_ASSEMBLIES} more (see the full report)")
        lines.append(" · ".join(shown))
    elif baseline and not changed_lines:
        lines.append(f"No assembly's coverage changed by more than {NOISE} lines.")
    if not_loaded:
        lines.append("Not loaded by any test that ran: " + ", ".join(
            f"{a} ({n} changed line{'s' if n != 1 else ''})" for a, n in sorted(not_loaded.items())))
    measured = f"Measured on Windows: {ran} · not measured: {NOT_MEASURED}"
    if any(key not in current and key not in PUSH_LANES for key in LANES):
        measured += ", lanes this PR didn't start"
    lines.append(measured)
    lines.append(change_source(args, baseline, common, failed, current))
    links = []
    if args.run_url:
        links.append(f"[Full report]({args.run_url})")
    if args.scope_url:
        links.append(f"[What is measured]({args.scope_url})")
    if links:
        lines.append(" · ".join(links))
    comment = MARKER + "\n" + "  \n".join(lines) + "\n"

    title = (f"{percent(covered_changed, coverable_changed)} of changed lines covered" if coverable_changed
             else "No coverable line changed")
    if lost_count:
        title += f" · {lost_text}"
    check_summary = plain("\n\n".join(lines))
    if annotation_count > MAX_ANNOTATIONS:
        check_summary += f"\n\nThe first {MAX_ANNOTATIONS} of {annotation_count} warnings are shown."
    check_text = None
    if view:
        more = f"the [full report]({args.run_url})" if args.run_url else "the full report"
        check_text = "\n".join(view_head + cut(view, MAX_CHECK_TEXT - len("\n".join(view_head)) - 1, len,
                                               lambda n: f"{plural(n, 'more file')} in {more}."))
    summary.append(comment.replace(MARKER + "\n", ""))
    tables = (assembly_table(total, covered_all, coverable_all, delta, change_all)
              + lane_table(current, baseline, failed))
    if view:
        def size(text):
            return len(text.encode("utf-8"))
        room = MAX_SUMMARY_BYTES - sum(size(line) + 1 for line in summary + tables + view_head)
        summary += [""] + view_head + cut(view, room, size, lambda n: (
            f"{plural(n, 'more file')} in `view.md` of the `coverage-report` artifact of this run."))
    summary += tables
    kind = "code" if changed_lines or changed_assemblies or not_loaded else "tests"
    check = {"title": title, "summary": check_summary, "annotations": annotations}
    if check_text:
        check["text"] = check_text
    result = {"kind": kind, "comment": comment, "check": check}
    if view:
        result["view"] = "\n".join(view_head + view)
    return result, summary


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--current", help="coverage-* artifacts of the run")
    parser.add_argument("--baseline", help="coverage-* artifacts of the base commit's master run, or of the push's parent")
    parser.add_argument("--diff", help="diff of the PR (merge commit against its first parent); none for a push")
    parser.add_argument("--projects", help="repo paths of the base commit (git ls-tree), to find a file's project")
    parser.add_argument("--source-commit", help="the commit the lanes tested, for the code in the view")
    parser.add_argument("--ci-label", help="markdown naming the CI run of a push, for the totals")
    parser.add_argument("--base-label", help="markdown naming the base run")
    parser.add_argument("--no-baseline", help="why the comment shows no change")
    parser.add_argument("--run-url", help="the report run, for the full report link")
    parser.add_argument("--scope-url", help="tests/COVERAGE.md")
    parser.add_argument("--not-updated", help="why this push got no report, for the comment of an earlier push")
    parser.add_argument("--previous", help="with --not-updated: the comment of the earlier push")
    parser.add_argument("--head", help="with --not-updated: the head commit of the PR")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    os.makedirs(args.out, exist_ok=True)
    if args.not_updated:
        with open(args.previous, encoding="utf-8") as file:
            comment = not_updated(file.read(), args.not_updated, args.head, args.run_url)
        if comment:
            with open(os.path.join(args.out, "comment.md"), "w", encoding="utf-8") as file:
                file.write(comment)
        return
    if not args.current:
        parser.error("--current is required")
    result, summary = report(args)
    view = result.pop("view", None)
    if view:
        with open(os.path.join(args.out, "view.md"), "w", encoding="utf-8") as file:
            file.write(view + "\n")
    with open(os.path.join(args.out, "result.json"), "w", encoding="utf-8") as file:
        json.dump(result, file, indent=1)
    with open(os.path.join(args.out, "summary.md"), "w", encoding="utf-8") as file:
        file.write("\n".join(summary) + "\n")
    if "comment" in result:
        with open(os.path.join(args.out, "comment.md"), "w", encoding="utf-8") as file:
            file.write(result["comment"])


if __name__ == "__main__":
    main()
