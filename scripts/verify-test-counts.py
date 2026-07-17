"""Fail the quality gate if retained Python or .NET tests disappear."""

from __future__ import annotations

import argparse
from pathlib import Path
import xml.etree.ElementTree as ElementTree


# GK-001 baseline: 45 pytest tests plus 4 unittest subtest cases.
MINIMUM_PYTHON_TESTS = 49
# GK-001 baseline: 17 facts plus 4 theory cases across the solution.
MINIMUM_DOTNET_TESTS = 21


def count_pytest_cases(result_file: Path) -> int:
    if not result_file.is_file():
        raise ValueError(f"pytest result file does not exist: {result_file}")

    root = ElementTree.parse(result_file).getroot()
    if root.tag == "testsuite":
        return int(root.attrib["tests"])

    suites = root.findall("./testsuite")
    if not suites:
        raise ValueError(f"pytest result file has no test suites: {result_file}")
    return sum(int(suite.attrib["tests"]) for suite in suites)


def count_dotnet_cases(results_directory: Path) -> int:
    result_files = sorted(results_directory.rglob("*.trx"))
    if not result_files:
        raise ValueError(f"no .NET TRX result files found below: {results_directory}")

    total = 0
    for result_file in result_files:
        root = ElementTree.parse(result_file).getroot()
        counters = root.find(".//{*}Counters")
        if counters is None or "total" not in counters.attrib:
            raise ValueError(f".NET result file has no total counter: {result_file}")
        total += int(counters.attrib["total"])
    return total


def require_minimum(label: str, actual: int, minimum: int) -> None:
    print(f"{label}: {actual} cases (required minimum: {minimum})")
    if actual < minimum:
        raise ValueError(
            f"{label} test count regressed: expected at least {minimum}, found {actual}"
        )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--python-results", type=Path)
    parser.add_argument("--dotnet-results", type=Path)
    args = parser.parse_args()
    if args.python_results is None and args.dotnet_results is None:
        parser.error("at least one result path is required")
    return args


def main() -> int:
    args = parse_args()
    try:
        if args.python_results is not None:
            require_minimum(
                "Python",
                count_pytest_cases(args.python_results),
                MINIMUM_PYTHON_TESTS,
            )
        if args.dotnet_results is not None:
            require_minimum(
                ".NET",
                count_dotnet_cases(args.dotnet_results),
                MINIMUM_DOTNET_TESTS,
            )
    except (ElementTree.ParseError, KeyError, TypeError, ValueError) as error:
        print(f"error: {error}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
