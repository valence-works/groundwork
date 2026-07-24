#!/usr/bin/env python3
"""Verify the non-promotable scheduled physical-storage benchmark evidence."""

from __future__ import annotations

import argparse
import hashlib
import itertools
import json
import pathlib
import re
from dataclasses import dataclass
from typing import Iterable


@dataclass(frozen=True)
class Provider:
    """A provider's command-line/artifact token and serialized enum token."""

    artifact_token: str
    request_token: str
    identity: str


PROVIDERS = (
    Provider("sqlite", "sqlite", "groundwork.sqlite"),
    Provider("sqlserver", "sqlServer", "groundwork.sql-server"),
    Provider("postgresql", "postgreSql", "groundwork.postgre-sql"),
    Provider("mongodb", "mongoDb", "groundwork.mongo-db"),
)
FORMS = {
    "shared": "sharedDocuments",
    "dedicated": "dedicatedDocumentTable",
    "entity": "physicalEntityTable",
}
DATASETS = (1000, 100000, 1000000)
WORKLOADS = (
    "clientResetPointReadBatch",
    "reusedClientPointReadBatch",
    "indexedQuery",
    "mixedCompoundOrdering",
    "insert",
    "update",
    "delete",
    "unitOfWork",
    "concurrentCreate",
    "optimisticConcurrency",
    "paginationAndCount",
    "backfillMigration",
    "clientRestartValidation",
    "storageGrowth",
)


@dataclass(frozen=True)
class VerificationMatrix:
    providers: tuple[Provider, ...]
    forms: tuple[str, ...]
    datasets: tuple[int, ...]
    workloads: tuple[str, ...]
    independent_runs: int


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=pathlib.Path, required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument(
        "--test-mode",
        action="store_true",
        help="Permit a deliberately narrowed matrix for executable verifier tests.",
    )
    parser.add_argument("--providers", help="Comma-separated artifact provider tokens (test mode only).")
    parser.add_argument("--forms", help="Comma-separated form tokens (test mode only).")
    parser.add_argument("--datasets", help="Comma-separated dataset sizes (test mode only).")
    parser.add_argument("--workloads", help="Comma-separated workload tokens (test mode only).")
    parser.add_argument("--independent-runs", type=int, help="Measured repetitions (test mode only).")
    args = parser.parse_args()
    narrowed_options = (args.providers, args.forms, args.datasets, args.workloads, args.independent_runs)
    if any(option is not None for option in narrowed_options) and not args.test_mode:
        parser.error("matrix overrides require --test-mode; production verification is fixed at 36 shards and 2,016 workers")
    return args


def comma_separated(value: str | None, defaults: Iterable[str], option: str) -> tuple[str, ...]:
    if value is None:
        return tuple(defaults)
    result = tuple(item.strip() for item in value.split(",") if item.strip())
    if not result:
        raise SystemExit(f"{option} cannot be empty")
    if len(set(result)) != len(result):
        raise SystemExit(f"{option} cannot contain duplicates")
    return result


def matrix_from_args(args: argparse.Namespace) -> VerificationMatrix:
    providers_by_token = {provider.artifact_token: provider for provider in PROVIDERS}
    provider_tokens = comma_separated(args.providers, providers_by_token, "--providers")
    unknown_providers = sorted(set(provider_tokens) - set(providers_by_token))
    if unknown_providers:
        raise SystemExit(f"unknown provider token(s): {unknown_providers}")

    forms = comma_separated(args.forms, FORMS, "--forms")
    unknown_forms = sorted(set(forms) - set(FORMS))
    if unknown_forms:
        raise SystemExit(f"unknown form token(s): {unknown_forms}")

    dataset_tokens = comma_separated(args.datasets, (str(dataset) for dataset in DATASETS), "--datasets")
    try:
        datasets = tuple(int(dataset) for dataset in dataset_tokens)
    except ValueError as error:
        raise SystemExit("--datasets must contain integers") from error
    if any(dataset <= 0 for dataset in datasets):
        raise SystemExit("--datasets must contain positive integers")

    workloads = comma_separated(args.workloads, WORKLOADS, "--workloads")
    unknown_workloads = sorted(set(workloads) - set(WORKLOADS))
    if unknown_workloads:
        raise SystemExit(f"unknown workload token(s): {unknown_workloads}")

    independent_runs = args.independent_runs if args.independent_runs is not None else 3
    if independent_runs <= 0:
        raise SystemExit("--independent-runs must be positive")
    return VerificationMatrix(
        tuple(providers_by_token[token] for token in provider_tokens),
        forms,
        datasets,
        workloads,
        independent_runs,
    )


def verify(root: pathlib.Path, run_id: str, matrix: VerificationMatrix) -> dict[str, object]:
    shards_root = root / "shards"
    expected_shards = {
        f"physical-storage-scheduled-{run_id}-{provider.artifact_token}-{form}-n{dataset}":
            (provider, form, dataset)
        for provider, form, dataset in itertools.product(matrix.providers, matrix.forms, matrix.datasets)
    }
    if not shards_root.is_dir():
        raise SystemExit(f"scheduled shard directory is missing: {shards_root}")
    actual_shards = {path.name for path in shards_root.iterdir() if path.is_dir()}
    if actual_shards != set(expected_shards):
        missing = sorted(set(expected_shards) - actual_shards)
        extra = sorted(actual_shards - set(expected_shards))
        raise SystemExit(f"scheduled shard set mismatch; missing={missing}, extra={extra}")

    expected_workers = {
        (provider.request_token, FORMS[form], dataset, workload, role, independent_run)
        for provider, form, dataset, workload in itertools.product(
            matrix.providers, matrix.forms, matrix.datasets, matrix.workloads)
        for role, independent_run in itertools.chain(
            (("untimedWarmup", 0),),
            (("measured", run) for run in range(1, matrix.independent_runs + 1)),
        )
    }
    actual_workers: set[tuple[object, ...]] = set()
    result_digests: dict[tuple[object, ...], set[str]] = {}
    workload_fingerprints: dict[tuple[object, ...], set[str]] = {}
    git_commits: set[str] = set()
    digest_pattern = re.compile(r"^[0-9a-f]{64}$")
    measured_count = 0
    expected_runs_per_shard = len(matrix.workloads) * (1 + matrix.independent_runs)

    for artifact_name, (provider, form, dataset) in sorted(expected_shards.items()):
        shard_root = shards_root / artifact_name / "evidence"
        manifest_path = shard_root / "run-group.json"
        if not manifest_path.is_file():
            raise SystemExit(f"{artifact_name} has no run-group.json")
        manifest = json.loads(manifest_path.read_text())
        if manifest.get("promotable") is not False:
            raise SystemExit(f"{artifact_name} makes a promotional claim")
        runs = manifest.get("runs", [])
        if len(runs) != expected_runs_per_shard:
            raise SystemExit(f"{artifact_name} has {len(runs)} workers; expected {expected_runs_per_shard}")

        for entry in runs:
            request_path = shard_root / entry["request"]
            response_path = shard_root / entry["response"]
            if not request_path.is_file() or not response_path.is_file():
                raise SystemExit(f"{artifact_name} has a missing request/response artifact")
            invocation = json.loads(request_path.read_text())
            response = json.loads(response_path.read_text())
            if response.get("succeeded") is not True:
                raise SystemExit(f"{artifact_name} contains a failed worker response")

            request = invocation["request"]
            shape = request["dataShape"]
            workload = request["workloads"][0]
            role = invocation["role"]
            independent_run = invocation["independentRun"]
            worker = (
                request["configuration"]["providers"][0],
                request["configuration"]["storageForms"][0],
                shape["datasetSize"],
                workload,
                role,
                independent_run,
            )
            if worker in actual_workers:
                raise SystemExit(f"duplicate scheduled worker tuple: {worker}")
            actual_workers.add(worker)

            if worker[0] != provider.request_token or worker[1] != FORMS[form] or worker[2] != dataset:
                raise SystemExit(f"{artifact_name} contains out-of-shard worker {worker}")
            if shape["payloadPaddingBytes"] != 0 or shape["querySelectivityBasisPoints"] != 5000:
                raise SystemExit(f"{artifact_name} contains an unexpected data shape")

            if role == "measured":
                measured_count += 1
                evidence_path = shard_root / entry["consumerEvidence"]
                evidence_bytes = evidence_path.read_bytes()
                evidence_digest = hashlib.sha256(evidence_bytes).hexdigest()
                if evidence_digest != entry["consumerEvidenceDigest"]:
                    raise SystemExit(f"{artifact_name} consumer evidence digest mismatch")
                evidence = json.loads(evidence_bytes)
                if evidence.get("promotable") is not False:
                    raise SystemExit(f"{artifact_name} measured evidence makes a promotional claim")
                git_commits.add(evidence["gitCommit"])
                result = evidence["results"]
                if len(result) != 1:
                    raise SystemExit(f"{artifact_name} worker does not contain exactly one result")
                result = result[0]
                expected_workload_identity = "groundwork.physical-storage/" + re.sub(
                    r"(?<!^)(?=[A-Z])", "-", workload).lower()
                if (
                    result["workloadIdentity"] != expected_workload_identity
                    or result["providerIdentity"] != provider.identity
                    or result["storageForm"] != FORMS[form]
                    or result["dataShape"] != shape
                    or result["independentRun"] != independent_run
                    or result["rawSampleCount"] < 1
                    or result["rawOperationLatencyCount"] < 1
                ):
                    raise SystemExit(f"{artifact_name} consumer evidence does not match worker {worker}")
                digest = result["resultDigest"]
                if not digest_pattern.fullmatch(digest):
                    raise SystemExit(f"{artifact_name} has an invalid result digest")
                digest_key = (
                    shape["datasetSize"],
                    shape["payloadPaddingBytes"],
                    shape["querySelectivityBasisPoints"],
                    workload,
                )
                result_digests.setdefault(digest_key, set()).add(digest)
                workload_fingerprints.setdefault(digest_key, set()).add(result["workloadFingerprint"])

    if actual_workers != expected_workers:
        missing = sorted(expected_workers - actual_workers)
        extra = sorted(actual_workers - expected_workers)
        raise SystemExit(f"scheduled worker coverage mismatch; missing={missing[:10]}, extra={extra[:10]}")
    unequal = {key: sorted(values) for key, values in result_digests.items() if len(values) != 1}
    if unequal:
        raise SystemExit(f"cross-provider/form observable results differ: {unequal}")
    fingerprint_drift = {
        key: sorted(values)
        for key, values in workload_fingerprints.items()
        if len(values) != 1
    }
    if fingerprint_drift:
        raise SystemExit(f"cross-provider/form workload fingerprints differ: {fingerprint_drift}")
    if len(git_commits) != 1:
        raise SystemExit(f"scheduled evidence spans multiple Git commits: {sorted(git_commits)}")

    return {
        "contract": "groundwork.physical-storage.scheduled-coverage/v1",
        "coverageVerified": True,
        "promotable": False,
        "requiredShardCount": len(expected_shards),
        "verifiedWorkerCount": len(actual_workers),
        "verifiedMeasuredWorkerCount": measured_count,
        "resultEqualityGroupCount": len(result_digests),
        "gitCommit": next(iter(git_commits)),
    }


def main() -> None:
    args = parse_args()
    verification = verify(args.root, args.run_id, matrix_from_args(args))
    (args.root / "coverage-verification.json").write_text(
        json.dumps(verification, indent=2, sort_keys=True) + "\n")
    print(json.dumps(verification, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
