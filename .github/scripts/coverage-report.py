#!/usr/bin/env python3
"""Coverage report of a CI run, for coverage-report.yml.

Reads the coverage-<lane> artifacts of the run (one Cobertura per lane, see .github/actions/coverage-lane) and,
for a PR, the run's diff and the artifacts of the base commit's master run (for a push, of its parent's run). Writes into --out:
  result.json  what to post: {"kind": "none" | "code" | "tests" | "push", "comment": markdown,
               "check": {"title", "summary", "annotations"}}
  comment.md   the comment of the PR, as in result.json
  summary.md   the step summary of the report job: the comment and the coverage of every assembly
The artifacts come from PR code, so everything read from them is data: assembly names, paths and lane names are
checked before they reach a comment. Standard library only, so it runs on any runner and locally:
  python3 .github/scripts/coverage-report.py --current <dir> [--baseline <dir>] [--diff <file>] --out <dir>
"""

import argparse
import colorsys
import json
import os
import re
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
MARKER = "<!-- coverage-report -->"

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
    """Coverage of one lane: per (assembly, file), line -> covered."""

    def __init__(self, path):
        self.files = defaultdict(dict)
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


def read_diff(path):
    """Lines the diff adds or changes, per new file path."""
    added = defaultdict(set)
    file = None
    number = 0
    in_hunk = False
    with open(path, encoding="utf-8", errors="replace") as diff:
        for line in diff:
            line = line.rstrip("\n")
            if line.startswith("diff --git "):
                file, in_hunk = None, False
            elif not in_hunk and line.startswith("+++ "):
                name = line[4:].strip().strip('"')
                file = name[2:] if name.startswith("b/") else None
            elif line.startswith("@@"):
                match = re.match(r"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", line)
                in_hunk = match is not None
                number = int(match.group(1)) if match else 0
            elif in_hunk and file:
                if line.startswith("+"):
                    added[file].add(number)
                    number += 1
                elif line.startswith(" ") or line == "":
                    number += 1
    return added


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


def code(text):
    """Text from the PR shown as inline code: no backticks or line breaks to break out of it."""
    return "`" + re.sub(r"[`\r\n]", "?", text) + "`"


def difference(now, before):
    """The change from [covered, coverable] before to now: (covered lines, points)."""
    return now[0] - before[0], (100 * now[0] / now[1] if now[1] else 0) - (100 * before[0] / before[1] if before[1] else 0)


def changes(current, baseline):
    """The lanes both runs have, and over them the change of each assembly and of all of them."""
    common = [key for key in current if key in baseline]
    if not common:
        return common, {}, None
    now = totals(merge(current[key] for key in common))
    before = totals(merge(baseline[key] for key in common))
    delta = {a: difference(now.get(a, [0, 0]), before.get(a, [0, 0])) for a in set(now) | set(before)}
    return common, delta, difference(*([sum(v[i] for v in t.values()) for i in (0, 1)] for t in (now, before)))


def change_source(args, baseline, common, current):
    """The line that says what the change is against, or why none is shown."""
    if not baseline:
        return f"No change shown: {args.no_baseline}" if args.no_baseline else "No change shown: no base run."
    change = f"Change against {args.base_label}" if args.base_label else "Change against the base commit"
    if set(common) != set(current):
        change += f", over {lane_names(common)} (the lanes both runs have)"
    return change


def table(total, covered_all, coverable_all, delta=None, change_all=None):
    """Step summary table of every assembly, with columns of the change of its coverage and covered lines when there
    is a base (blank when it didn't change)."""
    def row(name, covered, coverable, change):
        cells = [name, painted_percent(covered, coverable)]
        if delta is not None:
            moved = change and (change[0] or round(change[1], 1) != 0)
            cells.append(painted_change(signed_points(change[1]), change[0]) if moved else "")
        cells.append(str(covered))
        if delta is not None:
            cells.append(painted_change(signed_lines(change[0]), change[0]) if moved else "")
        return "| " + " | ".join(cells + [str(coverable)]) + " |"

    rows = ["", "| Assembly | Coverage | Change | Covered | Change | Coverable |" if delta is not None
            else "| Assembly | Coverage | Covered | Coverable |",
            "|---|--:|--:|--:|--:|--:|" if delta is not None else "|---|--:|--:|--:|"]
    rows += [row(a, covered, coverable, (delta or {}).get(a)) for a, (covered, coverable) in sorted(total.items())]
    rows.append(row("**All**", covered_all, coverable_all, change_all))
    return rows


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

    common, delta, change_all = changes(current, baseline)

    if not args.diff:
        head = f"## Coverage: {painted_percent(covered_all, coverable_all)} ({covered_all} of {coverable_all} lines)"
        if change_all:
            head += f" · {change_text(*change_all)}"
        summary += [head, "", f"Windows lanes: {ran or 'none'}" + (f" · not run: {', '.join(missing)}" if missing else "")]
        if args.ci_label:
            summary += ["", f"From the {args.ci_label}"]
        summary += ["", change_source(args, baseline, common, current)]
        return {"kind": "push"}, summary + table(total, covered_all, coverable_all, delta if common else None, change_all)

    # Changed files: product code with coverage, product code no test loaded, or tests
    projects = read_projects(args.projects)
    by_file = defaultdict(dict)
    assembly_of = defaultdict(set)
    for (assembly, file), lines in merged.items():
        assembly_of[file].add(assembly)
        target = by_file[file]
        for number, covered in lines.items():
            target[number] = target.get(number, False) or covered
    measured_assemblies = set(total)
    added = read_diff(args.diff)
    changed_assemblies = set()
    not_loaded = defaultdict(int)
    tests_changed = False
    changed_lines = {}
    for file, numbers in sorted(added.items()):
        if not file.endswith(".cs") or NOT_COVERED_SOURCE.search(file):
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
    uncovered_files = 0
    annotations = []
    for file, numbers in changed_lines.items():
        lines = by_file[file]
        coverable = sorted(n for n in numbers if n in lines)
        uncovered = [n for n in coverable if not lines[n]]
        coverable_changed += len(coverable)
        covered_changed += len(coverable) - len(uncovered)
        if not uncovered:
            continue
        uncovered_files += 1
        for first, last in ranges(uncovered, sorted(lines)):
            where = f"Line {first}" if first == last else f"Lines {first}-{last}"
            annotations.append({
                "path": file, "start_line": first, "end_line": last, "annotation_level": "warning",
                "title": "Not covered by tests",
                "message": f"{where}: no test of the Windows lanes that ran ({ran}) runs them.",
            })
    annotation_count = len(annotations)
    annotations = annotations[:MAX_ANNOTATIONS]

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
    if changed_lines or coverable_changed:
        head = f"**Coverage of changed lines: {painted_percent(covered_changed, coverable_changed)} ({covered_changed} of {coverable_changed})**"
        if coverable_changed - covered_changed:
            head += f" · {coverable_changed - covered_changed} not covered in {uncovered_files} file{'s' if uncovered_files != 1 else ''} (see the warnings)"
        lines.append(head)
    elif changed_assemblies:
        lines.append("**Coverage of changed lines:** no coverable line changed")
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
    lines.append(change_source(args, baseline, common, current))
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
    check_summary = plain("\n\n".join(lines))
    if annotation_count > MAX_ANNOTATIONS:
        check_summary += f"\n\nThe first {MAX_ANNOTATIONS} of {annotation_count} warnings are shown."
    summary.append(comment.replace(MARKER + "\n", ""))
    summary += table(total, covered_all, coverable_all, delta if common else None, change_all)
    kind = "code" if changed_lines or changed_assemblies or not_loaded else "tests"
    return {"kind": kind, "comment": comment,
            "check": {"title": title, "summary": check_summary, "annotations": annotations}}, summary


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--current", required=True, help="coverage-* artifacts of the run")
    parser.add_argument("--baseline", help="coverage-* artifacts of the base commit's master run, or of the push's parent")
    parser.add_argument("--diff", help="diff of the PR (merge commit against its first parent); none for a push")
    parser.add_argument("--projects", help="repo paths of the base commit (git ls-tree), to find a file's project")
    parser.add_argument("--ci-label", help="markdown naming the CI run of a push, for the totals")
    parser.add_argument("--base-label", help="markdown naming the base run")
    parser.add_argument("--no-baseline", help="why the comment shows no change")
    parser.add_argument("--run-url", help="the report run, for the full report link")
    parser.add_argument("--scope-url", help="tests/COVERAGE.md")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    result, summary = report(args)
    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, "result.json"), "w", encoding="utf-8") as file:
        json.dump(result, file, indent=1)
    with open(os.path.join(args.out, "summary.md"), "w", encoding="utf-8") as file:
        file.write("\n".join(summary) + "\n")
    if "comment" in result:
        with open(os.path.join(args.out, "comment.md"), "w", encoding="utf-8") as file:
            file.write(result["comment"])


if __name__ == "__main__":
    main()
