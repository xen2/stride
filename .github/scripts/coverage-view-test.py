"""Trial of two coverage views on a fake report (tmp/coverage-diff-view on xen2, not for master): annotations on the
check run, shown in the Files changed tab of a PR, and a head-only diff view in the check run text and the step
summary. Reads sample/coverage.json: per file and line, covered or not, at the head and at the base commit.

  python3 .github/scripts/coverage-view-test.py <base sha> <head sha> <check.json> <summary.md>
"""

import json
import re
import subprocess
import sys

CONTEXT = 2
LEGEND = ("`+` covered · `-` not covered · `!` covered at the base commit, not now · "
          "`new` the PR adds or changes the line · no mark: no code, or unchanged")


def read_diff(base, head):
    """Per head file: the lines the diff adds or changes, and the hunks (base start, base count, head start, head count)."""
    text = subprocess.run(["git", "diff", "--unified=0", base, head], capture_output=True, text=True, check=True).stdout
    added, hunks = {}, {}
    file = None
    for line in text.splitlines():
        if line.startswith("+++ "):
            name = line[4:]
            file = name[2:] if name.startswith("b/") else None
            if file:
                added.setdefault(file, set())
                hunks.setdefault(file, [])
        elif line.startswith("@@") and file:
            match = re.match(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", line)
            old, old_count, new, new_count = (int(g) if g is not None else 1 for g in match.groups())
            hunks[file].append((old, old_count, new, new_count))
            added[file] |= set(range(new, new + new_count))
    return added, hunks


def base_line(number, hunks):
    """The base line of an unchanged head line."""
    offset = 0
    for old, old_count, new, new_count in hunks:
        if new + new_count <= number:
            offset += new_count - old_count
    return number - offset


def ranges(marked, coverable, stops):
    """Ranges (first, last) of marked lines; a range stops at a coverable line for which stops() is true."""
    result, first, last = [], None, None
    for number in coverable:
        if number in marked:
            first = number if first is None else first
            last = number
        elif first is not None and stops(number):
            result.append((first, last))
            first = None
    if first is not None:
        result.append((first, last))
    return result


def fence(lines):
    longest = max((len(run) for line in lines for run in re.findall(r"`+", line)), default=0)
    return "`" * max(3, longest + 1)


def view(file, source, now, changed, lost):
    """The head lines around the changed and lost lines, as a diff block whose marks are the coverage."""
    shown = set()
    for number in changed | lost:
        shown |= set(range(number - CONTEXT, number + CONTEXT + 1))
    shown = sorted(n for n in shown if 1 <= n <= len(source))
    width = len(str(shown[-1]))
    groups, current = [], []
    for number in shown:
        if current and number != current[-1] + 1:
            groups.append(current)
            current = []
        current.append(number)
    groups.append(current)
    lines = []
    for group in groups:
        lines.append(f"@@ {file}, lines {group[0]}-{group[-1]} @@")
        for number in group:
            if number in lost:
                mark = "!"
            elif number in changed and number in now:
                mark = "+" if now[number] else "-"
            else:
                mark = " "
            new = "new" if number in changed else "   "
            lines.append(f"{mark} {number:>{width}} {new} │ {source[number - 1].expandtabs(4)}")
    marks = fence(lines)
    return [f"{marks}diff", *lines, marks]


def main():
    base, head, check_path, summary_path = sys.argv[1:5]
    with open("sample/coverage.json", encoding="utf-8") as data:
        coverage = json.load(data)
    added, hunks = read_diff(base, head)
    annotations, blocks = [], []
    covered_changed = coverable_changed = uncovered_files = lost_lines = lost_files = 0
    for file in sorted(coverage["head"]):
        now = {int(n): c for n, c in coverage["head"][file].items()}
        before = {int(n): c for n, c in coverage["base"].get(file, {}).items()}
        changed = added.get(file, set())
        coverable = sorted(now)
        uncovered = {n for n in changed if n in now and not now[n]}
        lost = {n for n in coverable if n not in changed and not now[n] and before.get(base_line(n, hunks.get(file, [])))}
        coverable_changed += sum(1 for n in changed if n in now)
        covered_changed += sum(1 for n in changed if now.get(n))
        uncovered_files += bool(uncovered)
        lost_lines += len(lost)
        lost_files += bool(lost)
        for first, last in ranges(uncovered, coverable, lambda n: now[n] or n not in changed):
            where = f"Line {first}" if first == last else f"Lines {first}-{last}"
            annotations.append({"path": file, "start_line": first, "end_line": last, "annotation_level": "warning",
                                "title": "Not covered by tests",
                                "message": f"{where}: no test of the Windows lanes that ran (D3D11, Simple) runs them."})
        for first, last in ranges(lost, coverable, lambda n: now[n]):
            where = f"Line {first}" if first == last else f"Lines {first}-{last}"
            annotations.append({"path": file, "start_line": first, "end_line": last, "annotation_level": "notice",
                                "title": "Coverage lost",
                                "message": f"{where}: tests ran them at the base commit, no test runs them now."})
        if uncovered or lost:
            with open(file, encoding="utf-8") as text:
                source = text.read().splitlines()
            blocks += [f"#### {file}", "", *view(file, source, now, changed, lost), ""]
    text = "\n".join(["### Changed and lost lines", "", LEGEND, "", *blocks])
    percent = f"{100 * covered_changed / coverable_changed:.0f}%"
    summary = (f"**Coverage of changed lines: {percent} ({covered_changed} of {coverable_changed})** · "
               f"{coverable_changed - covered_changed} not covered in {uncovered_files} file(s) · "
               f"{lost_lines} unchanged lines lost their coverage in {lost_files} file(s) (see the annotations)")
    with open(check_path, "w", encoding="utf-8") as out:
        json.dump({"name": "Coverage view test", "head_sha": head, "status": "completed", "conclusion": "neutral",
                   "output": {"title": f"{percent} of changed lines covered", "summary": summary, "text": text,
                              "annotations": annotations}}, out)
    with open(summary_path, "w", encoding="utf-8") as out:
        out.write(summary + "\n\n" + text + "\n")


if __name__ == "__main__":
    main()
