# Upstream checks

Every check in the tracked directories of AWS's [emulator evaluation suite](https://github.com/aws/aws-emulator-evaluation-suite),
and what became of it here. `tools/SyncEvaluationSuite` writes this file from upstream and the
ports' headers; see [README.md](README.md#keeping-up-with-upstream) for how to update the suite.

Pinned commit: [`4df2823`](https://github.com/aws/aws-emulator-evaluation-suite/tree/4df2823bafface2ada35f482c07ff6668cc078e1) (`4df2823bafface2ada35f482c07ff6668cc078e1`)

Edit by hand only:

- **Reasons:** the last cell of a check that isn't ported. Write one into a `**new**` row to
  decide not to port it.
- **Reviews:** set a `**review**` row (a check that isn't ported changed upstream) back to
  `not ported` once its reason still holds.
- **Directories:** add a `## behavioral/<directory>` heading to start tracking one.

Statuses: `ported`, `known gap: <FidelityGap>` (ported, skipped until fixed), `not ported`,
and the ones to act on: `**new**` (port it or give a reason), `**stale**` (upstream changed the
check since the commit in its port's header), `**review**` and `**removed upstream**` (delete
the port). The *Last changed* column is the last upstream commit to change the check.

## Summary

| Directory | Checks | Ported | Known gaps | Not ported | To do |
|---|---:|---:|---:|---:|---:|
| [`dynamodb`](#behavioraldynamodb) | 886 | 837 | 11 | 49 | 0 |
| [`dynamodbstreams`](#behavioraldynamodbstreams) | 16 | 16 | 0 | 0 | 0 |
| [`eventbridgev2`](#behavioraleventbridgev2) | 17 | 0 | 0 | 17 | 0 |
| [`eventbridgev2__iam__sns__sqs`](#behavioraleventbridgev2__iam__sns__sqs) | 2 | 0 | 0 | 2 | 0 |
| [`eventbridgev2__iam__sqs`](#behavioraleventbridgev2__iam__sqs) | 21 | 0 | 0 | 21 | 0 |
| [`events`](#behavioralevents) | 200 | 179 | 4 | 21 | 0 |
| [`events__iam__lambda__sqs`](#behavioralevents__iam__lambda__sqs) | 1 | 0 | 0 | 1 | 0 |
| [`events__iam__sns__sqs__sts`](#behavioralevents__iam__sns__sqs__sts) | 1 | 0 | 0 | 1 | 0 |
| [`events__iam__sqs__stepfunctions`](#behavioralevents__iam__sqs__stepfunctions) | 3 | 0 | 0 | 3 | 0 |
| [`events__iam__sts`](#behavioralevents__iam__sts) | 1 | 0 | 0 | 1 | 0 |
| [`events__sqs`](#behavioralevents__sqs) | 54 | 53 | 1 | 1 | 0 |
| [`sns`](#behavioralsns) | 245 | 207 | 1 | 38 | 0 |
| [`sns__sqs`](#behavioralsns__sqs) | 12 | 12 | 0 | 0 | 0 |
| [`sqs`](#behavioralsqs) | 306 | 271 | 3 | 35 | 0 |
| [`s3`](#behaviorals3) | 481 | 465 | 2 | 16 | 0 |
| [`s3__sqs`](#behaviorals3__sqs) | 16 | 13 | 0 | 3 | 0 |
| [`s3__sns__sqs`](#behaviorals3__sns__sqs) | 2 | 2 | 0 | 0 | 0 |
| [`events__s3__sqs`](#behavioralevents__s3__sqs) | 38 | 37 | 2 | 1 | 0 |
| **Total** | **2302** | **2092** | **24** | **210** | **0** |

### Not ported, by reason

| Reason | Checks |
|---|---:|
| EventBridge v2 | 40 |
| CloudFormation stacks | 38 |
| KMS keys | 25 |
| S3 (exports and imports) | 18 |
| IAM roles, users and identity policies | 17 |
| HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles | 13 |
| IAM role and identity-policy enforcement | 9 |
| IAM roles and role assumption | 9 |
| Kinesis streaming destinations | 8 |
| Application Auto Scaling | 6 |
| CloudWatch Logs delivery-status logging (IAM, Logs) | 4 |
| Firehose subscriptions (Firehose, S3, IAM) | 4 |
| IAM roles, role assumption and Lambda | 4 |
| Lambda targets | 4 |
| Step Functions targets | 4 |
| CloudWatch metrics | 2 |
| Lambda functions, with IAM roles | 2 |
| CloudWatch Logs targets | 1 |
| CloudWatch alarms | 1 |
| Kinesis targets | 1 |

### Known gaps, by root cause

| `FidelityGap` | Checks |
|---|---:|
| `EventBridgeUngrantedTargets` | 6 |
| `DynamoDbAuthentication` | 4 |
| `DynamoDbThroughput` | 4 |
| `EmulatorBudget` | 3 |
| `Other` | 2 |
| `SecureTransport` | 2 |
| `Validation` | 2 |
| `DynamoDbDetail` | 1 |

## behavioral/dynamodb

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `dynamodb.32-way-conditional-counter-no-lost-update-with-audit-trail` | `4df2823` | ported | `DynamoDb/Check32WayConditionalCounterNoLostUpdateWithAuditTrail.cs` |
| `dynamodb.50-table-fanout-item-isolation-and-scan-exactness` | `4df2823` | ported | `DynamoDb/Check50TableFanoutItemIsolationAndScanExactness.cs` |
| `dynamodb.absence-condition-holds-with-no-previous-item` | `4df2823` | ported | `DynamoDb/AbsenceConditionHoldsWithNoPreviousItem.cs` |
| `dynamodb.all-projected-attributes-select-serves-include-index-projection` | `4df2823` | ported | `DynamoDb/AllProjectedAttributesSelectServesIncludeIndexProjection.cs` |
| `dynamodb.atomic-counter-and-set-add-survive-concurrent-updates` | `4df2823` | ported | `DynamoDb/AtomicCounterAndSetAddSurviveConcurrentUpdates.cs` |
| `dynamodb.atomic-counter-on-missing-attribute` | `4df2823` | ported | `DynamoDb/AtomicCounterOnMissingAttribute.cs` |
| `dynamodb.atomic-decrement` | `4df2823` | ported | `DynamoDb/AtomicDecrement.cs` |
| `dynamodb.atomic-increment` | `4df2823` | ported | `DynamoDb/AtomicIncrement.cs` |
| `dynamodb.attribute-name-length-limit` | `4df2823` | ported | `DynamoDb/AttributeNameLengthLimit.cs` |
| `dynamodb.attribute-names-are-case-sensitive` | `4df2823` | ported | `DynamoDb/AttributeNamesAreCaseSensitive.cs` |
| `dynamodb.attribute-updates-add-delete-on-absent-item` | `4df2823` | ported | `DynamoDb/AttributeUpdatesAddDeleteOnAbsentItem.cs` |
| `dynamodb.attribute-updates-add-delete-put-return-values-on-existing-item` | `4df2823` | ported | `DynamoDb/AttributeUpdatesAddDeletePutReturnValuesOnExistingItem.cs` |
| `dynamodb.attribute-updates-update-item-item-size-409600-byte-boundary` | `4df2823` | ported | `DynamoDb/AttributeUpdatesUpdateItemItemSize409600ByteBoundary.cs` |
| `dynamodb.attribute-value-union-requires-exactly-one-member` | `4df2823` | ported | `DynamoDb/AttributeValueUnionRequiresExactlyOneMember.cs` |
| `dynamodb.attribute-value-union-requires-exactly-one-member-rejected` | `4df2823` | ported | `DynamoDb/AttributeValueUnionRequiresExactlyOneMemberRejected.cs` |
| `dynamodb.autoscaling-and-kinesis-destination-defaults` | `4df2823` | ported | `DynamoDb/AutoscalingAndKinesisDestinationDefaults.cs` |
| `dynamodb.awsjson-epoch-timestamp-survives-two-deserializers` | `4df2823` | not ported | IAM roles, role assumption and Lambda |
| `dynamodb.awsjson-error-code-carrier-is-body-only-rejected` | `4df2823` | not ported | IAM roles, role assumption and Lambda |
| `dynamodb.backup-create-describe-delete-lifecycle` | `4df2823` | ported | `DynamoDb/BackupCreateDescribeDeleteLifecycle.cs` |
| `dynamodb.backup-create-describe-list-delete-404` | `4df2823` | ported | `DynamoDb/BackupCreateDescribeListDelete404.cs` |
| `dynamodb.backup-lifecycle-create-describe-delete-404` | `4df2823` | ported | `DynamoDb/BackupLifecycleCreateDescribeDelete404.cs` |
| `dynamodb.backup-not-found` | `4df2823` | ported | `DynamoDb/BackupNotFound.cs` |
| `dynamodb.batch-execute-statement-absent-table-per-slot-resource-not-found` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementAbsentTablePerSlotResourceNotFound.cs` |
| `dynamodb.batch-execute-statement-condition-and-duplicate-failures-echo-table-name` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementConditionAndDuplicateFailuresEchoTableName.cs` |
| `dynamodb.batch-execute-statement-condition-failure-returns-item-in-error` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementConditionFailureReturnsItemInError.cs` |
| `dynamodb.batch-execute-statement-consistent-read-is-billed-per-member` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementConsistentReadIsBilledPerMember.cs` |
| `dynamodb.batch-execute-statement-index-qualified-select-rejected-per-slot` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementIndexQualifiedSelectRejectedPerSlot.cs` |
| `dynamodb.batch-execute-statement-malformed-slot-siblings-execute` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementMalformedSlotSiblingsExecute.cs` |
| `dynamodb.batch-execute-statement-multi-attribute-gsi-select-rejected-per-slot` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementMultiAttributeGsiSelectRejectedPerSlot.cs` |
| `dynamodb.batch-execute-statement-non-key-and-index-key-select-rejected-no-table-name` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementNonKeyAndIndexKeySelectRejectedNoTableName.cs` |
| `dynamodb.batch-execute-statement-or-sort-key-overlap-rejected-per-slot` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementOrSortKeyOverlapRejectedPerSlot.cs` |
| `dynamodb.batch-execute-statement-return-consumed-capacity-enum-rejected` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementReturnConsumedCapacityEnumRejected.cs` |
| `dynamodb.batch-execute-statement-returning-delete-old-and-update-remove-modified-new` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementReturningDeleteOldAndUpdateRemoveModifiedNew.cs` |
| `dynamodb.batch-execute-statement-returning-update-vs-delete-rejected` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementReturningUpdateVsDeleteRejected.cs` |
| `dynamodb.batch-execute-statement-set-add-no-error-slot` | `4df2823` | ported | `DynamoDb/BatchExecuteStatementSetAddNoErrorSlot.cs` |
| `dynamodb.batch-execute-statement-throttling-enforced` | `4df2823` | known gap: DynamoDbThroughput | `DynamoDb/BatchExecuteStatementThrottlingEnforced.cs` |
| `dynamodb.batch-get-item-always-reports-unprocessed-keys` | `4df2823` | ported | `DynamoDb/BatchGetItemAlwaysReportsUnprocessedKeys.cs` |
| `dynamodb.batch-get-item-duplicate-keys-rejected` | `4df2823` | ported | `DynamoDb/BatchGetItemDuplicateKeysRejected.cs` |
| `dynamodb.batch-get-item-expression-and-legacy-parameter-rules` | `4df2823` | ported | `DynamoDb/BatchGetItemExpressionAndLegacyParameterRules.cs` |
| `dynamodb.batch-get-item-multi-table-absent-keys-return-empty-list` | `4df2823` | ported | `DynamoDb/BatchGetItemMultiTableAbsentKeysReturnEmptyList.cs` |
| `dynamodb.batch-get-item-over-the-hundred-key-cap-rejected` | `4df2823` | ported | `DynamoDb/BatchGetItemOverTheHundredKeyCapRejected.cs` |
| `dynamodb.batch-get-item-oversized-echoes-consistent-read-in-unprocessed-keys` | `4df2823` | ported | `DynamoDb/BatchGetItemOversizedEchoesConsistentReadInUnprocessedKeys.cs` |
| `dynamodb.batch-get-item-returns-complete-set` | `4df2823` | ported | `DynamoDb/BatchGetItemReturnsCompleteSet.cs` |
| `dynamodb.batch-get-item-rounds-each-item-to-4kb-before-summing` | `4df2823` | ported | `DynamoDb/BatchGetItemRoundsEachItemTo4kbBeforeSumming.cs` |
| `dynamodb.batch-get-item-unknown-table-rejected` | `4df2823` | ported | `DynamoDb/BatchGetItemUnknownTableRejected.cs` |
| `dynamodb.batch-get-item-unprocessed-keys-are-in-storage-and-drain-to-the-exact-set` | `4df2823` | ported | `DynamoDb/BatchGetItemUnprocessedKeysAreInStorageAndDrainToTheExactSet.cs` |
| `dynamodb.batch-get-items` | `4df2823` | ported | `DynamoDb/BatchGetItems.cs` |
| `dynamodb.batch-get-multi-table` | `4df2823` | ported | `DynamoDb/BatchGetMultiTable.cs` |
| `dynamodb.batch-get-unprocessed-keys` | `4df2823` | ported | `DynamoDb/BatchGetUnprocessedKeys.cs` |
| `dynamodb.batch-request-items-key-not-a-table-name-rejected` | `4df2823` | ported | `DynamoDb/BatchRequestItemsKeyNotATableNameRejected.cs` |
| `dynamodb.batch-request-shape-defects-rejected` | `4df2823` | ported | `DynamoDb/BatchRequestShapeDefectsRejected.cs` |
| `dynamodb.batch-return-options-invalid-enum-rejected` | `4df2823` | ported | `DynamoDb/BatchReturnOptionsInvalidEnumRejected.cs` |
| `dynamodb.batch-write-duplicate-key-rejected` | `4df2823` | ported | `DynamoDb/BatchWriteDuplicateKeyRejected.cs` |
| `dynamodb.batch-write-item-and-delete-item-index-capacity-arms-follow-item-estate` | `4df2823` | known gap: EmulatorBudget | `DynamoDb/BatchWriteItemAndDeleteItemIndexCapacityArmsFollowItemEstate.cs` |
| `dynamodb.batch-write-item-delete-of-a-missing-item-still-consumes-a-write-unit` | `4df2823` | ported | `DynamoDb/BatchWriteItemDeleteOfAMissingItemStillConsumesAWriteUnit.cs` |
| `dynamodb.batch-write-item-full-batch-accepted` | `4df2823` | ported | `DynamoDb/BatchWriteItemFullBatchAccepted.cs` |
| `dynamodb.batch-write-item-gsi-key-type-mismatch-rejected` | `4df2823` | ported | `DynamoDb/BatchWriteItemGsiKeyTypeMismatchRejected.cs` |
| `dynamodb.batch-write-item-key-defects-message-diverges-from-put-item` | `4df2823` | ported | `DynamoDb/BatchWriteItemKeyDefectsMessageDivergesFromPutItem.cs` |
| `dynamodb.batch-write-item-mixed-put-delete` | `4df2823` | ported | `DynamoDb/BatchWriteItemMixedPutDelete.cs` |
| `dynamodb.batch-write-item-multi-table-put-and-delete` | `4df2823` | ported | `DynamoDb/BatchWriteItemMultiTablePutAndDelete.cs` |
| `dynamodb.batch-write-item-multi-table-unknown-table-rejected` | `4df2823` | ported | `DynamoDb/BatchWriteItemMultiTableUnknownTableRejected.cs` |
| `dynamodb.batch-write-item-oversized-item-among-valid-batch-rejected-whole` | `4df2823` | ported | `DynamoDb/BatchWriteItemOversizedItemAmongValidBatchRejectedWhole.cs` |
| `dynamodb.batch-write-item-put-and-delete-for-the-same-key-rejected` | `4df2823` | ported | `DynamoDb/BatchWriteItemPutAndDeleteForTheSameKeyRejected.cs` |
| `dynamodb.batch-write-item-put-request-accepts-32-levels-rejects-33` | `4df2823` | ported | `DynamoDb/BatchWriteItemPutRequestAccepts32LevelsRejects33.cs` |
| `dynamodb.batch-write-item-rejects-whole-call-for-one-oversized-nested-put` | `4df2823` | ported | `DynamoDb/BatchWriteItemRejectsWholeCallForOneOversizedNestedPut.cs` |
| `dynamodb.batch-write-item-rounds-each-item-to-1kb-then-sums` | `4df2823` | ported | `DynamoDb/BatchWriteItemRoundsEachItemTo1kbThenSums.cs` |
| `dynamodb.batch-write-item-throttle-accounting-loses-no-key` | `4df2823` | known gap: DynamoDbThroughput | `DynamoDb/BatchWriteItemThrottleAccountingLosesNoKey.cs` |
| `dynamodb.batch-write-over-25-rejected` | `4df2823` | ported | `DynamoDb/BatchWriteOver25Rejected.cs` |
| `dynamodb.batch-write-put-25` | `4df2823` | ported | `DynamoDb/BatchWritePut25.cs` |
| `dynamodb.batchwriteitem-request-over-16mib-rejected-413-before-auth` | `4df2823` | known gap: DynamoDbAuthentication | `DynamoDb/BatchwriteitemRequestOver16mibRejected413BeforeAuth.cs` |
| `dynamodb.binary-range-key-item-set-and-nested-path-updates-then-table-refuses-reads` | `4df2823` | ported | `DynamoDb/BinaryRangeKeyItemSetAndNestedPathUpdatesThenTableRefusesReads.cs` |
| `dynamodb.binary-sort-key-orders-by-unsigned-bytes` | `4df2823` | ported | `DynamoDb/BinarySortKeyOrdersByUnsignedBytes.cs` |
| `dynamodb.bool-and-null-attribute-byte-cost-at-item-size-gate` | `4df2823` | ported | `DynamoDb/BoolAndNullAttributeByteCostAtItemSizeGate.cs` |
| `dynamodb.capacity-mode-switches-both-ways` | `4df2823` | ported | `DynamoDb/CapacityModeSwitchesBothWays.cs` |
| `dynamodb.cli-create-sdk-read-dynamodb` | `4df2823` | ported | `DynamoDb/CliCreateSdkReadDynamodb.cs` |
| `dynamodb.cli-dynamodb-get-nonexistent-table-error` | `4df2823` | ported | `DynamoDb/CliDynamodbGetNonexistentTableError.cs` |
| `dynamodb.cli-dynamodb-put-get-item` | `4df2823` | ported | `DynamoDb/CliDynamodbPutGetItem.cs` |
| `dynamodb.cli-query-nested-extraction` | `4df2823` | ported | `DynamoDb/CliQueryNestedExtraction.cs` |
| `dynamodb.composite-gsi-omits-item-missing-only-the-sort-key` | `4df2823` | ported | `DynamoDb/CompositeGsiOmitsItemMissingOnlyTheSortKey.cs` |
| `dynamodb.conc-ddb-optimistic-cancelled-token-is-not-burned` | `4df2823` | ported | `DynamoDb/ConcDdbOptimisticCancelledTokenIsNotBurned.cs` |
| `dynamodb.conc-ddb-optimistic-delete-race-losers-see-no-item` | `4df2823` | ported | `DynamoDb/ConcDdbOptimisticDeleteRaceLosersSeeNoItem.cs` |
| `dynamodb.conc-ddb-optimistic-loser-all-old-is-winner-post-image-rejected` | `4df2823` | ported | `DynamoDb/ConcDdbOptimisticLoserAllOldIsWinnerPostImageRejected.cs` |
| `dynamodb.conc-ddb-optimistic-put-race-winner-payload-intact` | `4df2823` | ported | `DynamoDb/ConcDdbOptimisticPutRaceWinnerPayloadIntact.cs` |
| `dynamodb.conc-ddb-optimistic-token-fingerprint-covers-return-options-rejected` | `4df2823` | ported | `DynamoDb/ConcDdbOptimisticTokenFingerprintCoversReturnOptionsRejected.cs` |
| `dynamodb.conc-ddb-optimistic-transact-check-and-update-same-key-rejected` | `4df2823` | ported | `DynamoDb/ConcDdbOptimisticTransactCheckAndUpdateSameKeyRejected.cs` |
| `dynamodb.conc-ddb-optimistic-transact-race-cancelled-leaves-no-partial-write` | `4df2823` | ported | `DynamoDb/ConcDdbOptimisticTransactRaceCancelledLeavesNoPartialWrite.cs` |
| `dynamodb.conc-ddb-optimistic-version-race-one-winner-per-round` | `4df2823` | ported | `DynamoDb/ConcDdbOptimisticVersionRaceOneWinnerPerRound.cs` |
| `dynamodb.concurrent-create-backup-duplicate-name-accepted` | `4df2823` | ported | `DynamoDb/ConcurrentCreateBackupDuplicateNameAccepted.cs` |
| `dynamodb.concurrent-create-table-single-winner` | `4df2823` | ported | `DynamoDb/ConcurrentCreateTableSingleWinner.cs` |
| `dynamodb.concurrent-delete-backup-single-winner` | `4df2823` | ported | `DynamoDb/ConcurrentDeleteBackupSingleWinner.cs` |
| `dynamodb.concurrent-disjoint-nested-map-sets-all-land` | `4df2823` | ported | `DynamoDb/ConcurrentDisjointNestedMapSetsAllLand.cs` |
| `dynamodb.concurrent-global-table-replica-update-consistency` | `4df2823` | ported | `DynamoDb/ConcurrentGlobalTableReplicaUpdateConsistency.cs` |
| `dynamodb.concurrent-put-item-last-writer-wins-without-condition` | `4df2823` | ported | `DynamoDb/ConcurrentPutItemLastWriterWinsWithoutCondition.cs` |
| `dynamodb.concurrent-table-replica-auto-scaling-race-no-corruption` | `4df2823` | not ported | Application Auto Scaling |
| `dynamodb.concurrent-tag-resource-no-lost-tags` | `4df2823` | ported | `DynamoDb/ConcurrentTagResourceNoLostTags.cs` |
| `dynamodb.concurrent-unconditional-list-append-loses-no-element` | `4df2823` | ported | `DynamoDb/ConcurrentUnconditionalListAppendLosesNoElement.cs` |
| `dynamodb.condition-and-or-not` | `4df2823` | ported | `DynamoDb/ConditionAndOrNot.cs` |
| `dynamodb.condition-attribute-exists` | `4df2823` | ported | `DynamoDb/ConditionAttributeExists.cs` |
| `dynamodb.condition-attribute-not-exists` | `4df2823` | ported | `DynamoDb/ConditionAttributeNotExists.cs` |
| `dynamodb.condition-attribute-type-gates-the-write` | `4df2823` | ported | `DynamoDb/ConditionAttributeTypeGatesTheWrite.cs` |
| `dynamodb.condition-comparison-operators` | `4df2823` | ported | `DynamoDb/ConditionComparisonOperators.cs` |
| `dynamodb.condition-contains-set-element-gates-update-and-delete` | `4df2823` | ported | `DynamoDb/ConditionContainsSetElementGatesUpdateAndDelete.cs` |
| `dynamodb.condition-expression-contains-same-path-and-between-reversed-rejected` | `4df2823` | ported | `DynamoDb/ConditionExpressionContainsSamePathAndBetweenReversedRejected.cs` |
| `dynamodb.condition-nested-document-paths-that-hold-apply-the-write` | `4df2823` | ported | `DynamoDb/ConditionNestedDocumentPathsThatHoldApplyTheWrite.cs` |
| `dynamodb.condition-nested-path-type-mismatch-fails-the-condition` | `4df2823` | ported | `DynamoDb/ConditionNestedPathTypeMismatchFailsTheCondition.cs` |
| `dynamodb.condition-non-orderable-path-and-size-of-unsupported-type-evaluate-false` | `4df2823` | ported | `DynamoDb/ConditionNonOrderablePathAndSizeOfUnsupportedTypeEvaluateFalse.cs` |
| `dynamodb.condition-on-missing-item` | `4df2823` | ported | `DynamoDb/ConditionOnMissingItem.cs` |
| `dynamodb.condition-ordering-comparison-non-orderable-literal-rejected` | `4df2823` | ported | `DynamoDb/ConditionOrderingComparisonNonOrderableLiteralRejected.cs` |
| `dynamodb.condition-size-counts-string-length-not-bytes` | `4df2823` | ported | `DynamoDb/ConditionSizeCountsStringLengthNotBytes.cs` |
| `dynamodb.conditional-check-failed-requests-metric-counts-only-the-failures` | `4df2823` | not ported | CloudWatch metrics |
| `dynamodb.conditionaloperator-enum-error-precedes-nesting-depth-in-legacy-expected` | `4df2823` | ported | `DynamoDb/ConditionaloperatorEnumErrorPrecedesNestingDepthInLegacyExpected.cs` |
| `dynamodb.consumed-capacity-binary-attribute-counts-raw-bytes-not-base64` | `4df2823` | ported | `DynamoDb/ConsumedCapacityBinaryAttributeCountsRawBytesNotBase64.cs` |
| `dynamodb.consumed-capacity-byte-exact-4096-boundary-with-mixed-type-item` | `4df2823` | ported | `DynamoDb/ConsumedCapacityByteExact4096BoundaryWithMixedTypeItem.cs` |
| `dynamodb.consumed-capacity-halves-for-eventually-consistent-read` | `4df2823` | ported | `DynamoDb/ConsumedCapacityHalvesForEventuallyConsistentRead.cs` |
| `dynamodb.consumed-capacity-indexes-breakdown` | `4df2823` | ported | `DynamoDb/ConsumedCapacityIndexesBreakdown.cs` |
| `dynamodb.consumed-capacity-indexes-breakdown-sums-base-plus-lsi-writes` | `4df2823` | ported | `DynamoDb/ConsumedCapacityIndexesBreakdownSumsBasePlusLsiWrites.cs` |
| `dynamodb.consumed-capacity-indexes-breakdown-sums-to-total-over-lsi-fanout` | `4df2823` | ported | `DynamoDb/ConsumedCapacityIndexesBreakdownSumsToTotalOverLsiFanout.cs` |
| `dynamodb.consumed-capacity-rounds-reads-to-4kb-boundaries` | `4df2823` | ported | `DynamoDb/ConsumedCapacityRoundsReadsTo4kbBoundaries.cs` |
| `dynamodb.contributor-insights-disable-while-enabling-rejected` | `4df2823` | ported | `DynamoDb/ContributorInsightsDisableWhileEnablingRejected.cs` |
| `dynamodb.contributor-insights-enable-describe-list` | `4df2823` | ported | `DynamoDb/ContributorInsightsEnableDescribeList.cs` |
| `dynamodb.contributor-insights-lifecycle` | `4df2823` | ported | `DynamoDb/ContributorInsightsLifecycle.cs` |
| `dynamodb.contributor-insights-not-found` | `4df2823` | ported | `DynamoDb/ContributorInsightsNotFound.cs` |
| `dynamodb.create-backup-nonexistent-table` | `4df2823` | ported | `DynamoDb/CreateBackupNonexistentTable.cs` |
| `dynamodb.create-describe-delete` | `4df2823` | ported | `DynamoDb/CreateDescribeDelete.cs` |
| `dynamodb.create-global-table-already-exists-rejected` | `4df2823` | ported | `DynamoDb/CreateGlobalTableAlreadyExistsRejected.cs` |
| `dynamodb.create-table-exceeds-limit-rejected` | `4df2823` | ported | `DynamoDb/CreateTableExceedsLimitRejected.cs` |
| `dynamodb.create-table-gsi-key-not-in-attribute-definitions-rejected` | `4df2823` | ported | `DynamoDb/CreateTableGsiKeyNotInAttributeDefinitionsRejected.cs` |
| `dynamodb.create-table-gsi-key-schema-hash-after-range-rejected` | `4df2823` | ported | `DynamoDb/CreateTableGsiKeySchemaHashAfterRangeRejected.cs` |
| `dynamodb.create-table-invalid-key-schema-fails` | `4df2823` | ported | `DynamoDb/CreateTableInvalidKeySchemaFails.cs` |
| `dynamodb.create-table-keys-only-gsi-with-non-key-attributes-rejected` | `4df2823` | ported | `DynamoDb/CreateTableKeysOnlyGsiWithNonKeyAttributesRejected.cs` |
| `dynamodb.create-table-lsi-hash-key-differs-from-table-hash-key-rejected` | `4df2823` | ported | `DynamoDb/CreateTableLsiHashKeyDiffersFromTableHashKeyRejected.cs` |
| `dynamodb.create-table-lsi-sort-key-not-in-attribute-definitions-rejected` | `4df2823` | ported | `DynamoDb/CreateTableLsiSortKeyNotInAttributeDefinitionsRejected.cs` |
| `dynamodb.create-table-lsi-without-range-key` | `4df2823` | ported | `DynamoDb/CreateTableLsiWithoutRangeKey.cs` |
| `dynamodb.create-table-on-demand-throughput-below-minimum-rejected` | `4df2823` | ported | `DynamoDb/CreateTableOnDemandThroughputBelowMinimumRejected.cs` |
| `dynamodb.create-table-provisioned-without-throughput` | `4df2823` | ported | `DynamoDb/CreateTableProvisionedWithoutThroughput.cs` |
| `dynamodb.create-table-rejects-invalid-table-class-value` | `4df2823` | ported | `DynamoDb/CreateTableRejectsInvalidTableClassValue.cs` |
| `dynamodb.create-table-rejects-malformed-requests` | `4df2823` | ported | `DynamoDb/CreateTableRejectsMalformedRequests.cs` |
| `dynamodb.create-table-sse-enabled-without-kms-key-uses-aws-managed-key` | `4df2823` | not ported | KMS keys |
| `dynamodb.create-table-table-class-reported-at-create-and-describe` | `4df2823` | ported | `DynamoDb/CreateTableTableClassReportedAtCreateAndDescribe.cs` |
| `dynamodb.create-table-unused-attribute-definitions` | `4df2823` | ported | `DynamoDb/CreateTableUnusedAttributeDefinitions.cs` |
| `dynamodb.create-table-vector-index-include-projection-requires-non-key-attributes-rejected` | `4df2823` | ported | `DynamoDb/CreateTableVectorIndexIncludeProjectionRequiresNonKeyAttributesRejected.cs` |
| `dynamodb.create-table-vector-index-two-hash-search-schema-elements-rejected` | `4df2823` | ported | `DynamoDb/CreateTableVectorIndexTwoHashSearchSchemaElementsRejected.cs` |
| `dynamodb.create-table-vector-index-validation-rejected` | `4df2823` | ported | `DynamoDb/CreateTableVectorIndexValidationRejected.cs` |
| `dynamodb.create-table-with-gsi` | `4df2823` | ported | `DynamoDb/CreateTableWithGsi.cs` |
| `dynamodb.create-table-with-lsi` | `4df2823` | ported | `DynamoDb/CreateTableWithLsi.cs` |
| `dynamodb.create-table-with-six-local-secondary-indexes-rejected` | `4df2823` | ported | `DynamoDb/CreateTableWithSixLocalSecondaryIndexesRejected.cs` |
| `dynamodb.create-table-with-vector-index-is-described-back` | `4df2823` | ported | `DynamoDb/CreateTableWithVectorIndexIsDescribedBack.cs` |
| `dynamodb.ddb-eventual-and-strong-read-are-two-different-reads` | `4df2823` | ported | `DynamoDb/DdbEventualAndStrongReadAreTwoDifferentReads.cs` |
| `dynamodb.ddb-execute-statement-and-transaction-against-absent-table` | `4df2823` | ported | `DynamoDb/DdbExecuteStatementAndTransactionAgainstAbsentTable.cs` |
| `dynamodb.ddb-frc-cross-type-string-operand-eq-empty-ne-everything` | `4df2823` | ported | `DynamoDb/DdbFrcCrossTypeStringOperandEqEmptyNeEverything.cs` |
| `dynamodb.ddb-ltr-fresh-table-omits-increase-and-decrease-timestamps` | `4df2823` | ported | `DynamoDb/DdbLtrFreshTableOmitsIncreaseAndDecreaseTimestamps.cs` |
| `dynamodb.ddb-numeric-sort-key-magnitude-ceiling-rejected` | `4df2823` | ported | `DynamoDb/DdbNumericSortKeyMagnitudeCeilingRejected.cs` |
| `dynamodb.ddb-numeric-sort-key-query-orders-at-magnitude-extremes` | `4df2823` | ported | `DynamoDb/DdbNumericSortKeyQueryOrdersAtMagnitudeExtremes.cs` |
| `dynamodb.ddb-partiql-is-type-name-filters-bool-and-string-attributes` | `4df2823` | ported | `DynamoDb/DdbPartiqlIsTypeNameFiltersBoolAndStringAttributes.cs` |
| `dynamodb.ddb-partiql-is-type-name-rejects-update-leaves-item-unchanged` | `4df2823` | ported | `DynamoDb/DdbPartiqlIsTypeNameRejectsUpdateLeavesItemUnchanged.cs` |
| `dynamodb.ddb-query-filter-size-counts-bytes-elements-or-keys-by-attribute-type` | `4df2823` | ported | `DynamoDb/DdbQueryFilterSizeCountsBytesElementsOrKeysByAttributeType.cs` |
| `dynamodb.ddb-query-size-function-rejected-in-key-condition` | `4df2823` | ported | `DynamoDb/DdbQuerySizeFunctionRejectedInKeyCondition.cs` |
| `dynamodb.ddbctl-table-refuses-changes-while-creating-and-deleting` | `4df2823` | ported | `DynamoDb/DdbctlTableRefusesChangesWhileCreatingAndDeleting.cs` |
| `dynamodb.ddbglobal-legacy-2017-global-table-apis-rejected` | `4df2823` | ported | `DynamoDb/DdbglobalLegacy2017GlobalTableApisRejected.cs` |
| `dynamodb.ddbglobal-replica-delete-for-absent-replica-rejected` | `4df2823` | ported | `DynamoDb/DdbglobalReplicaDeleteForAbsentReplicaRejected.cs` |
| `dynamodb.ddbglobal-replica-updates-local-region-rejected` | `4df2823` | ported | `DynamoDb/DdbglobalReplicaUpdatesLocalRegionRejected.cs` |
| `dynamodb.ddbgsi-base-item-delete-removes-the-index-entry-while-a-sibling-survives` | `4df2823` | ported | `DynamoDb/DdbgsiBaseItemDeleteRemovesTheIndexEntryWhileASiblingSurvives.cs` |
| `dynamodb.ddbgsi-index-entries-do-not-cross-into-a-derived-second-region` | `4df2823` | ported | `DynamoDb/DdbgsiIndexEntriesDoNotCrossIntoADerivedSecondRegion.cs` |
| `dynamodb.ddbgsi-index-gains-no-entry-when-a-conditional-write-is-rejected` | `4df2823` | ported | `DynamoDb/DdbgsiIndexGainsNoEntryWhenAConditionalWriteIsRejected.cs` |
| `dynamodb.ddbgsi-projected-value-converges-in-the-index-after-an-update` | `4df2823` | ported | `DynamoDb/DdbgsiProjectedValueConvergesInTheIndexAfterAnUpdate.cs` |
| `dynamodb.ddbgsi-query-filter-on-a-non-projected-attribute-rejected-while-scan-matches-nothing` | `4df2823` | ported | `DynamoDb/DdbgsiQueryFilterOnANonProjectedAttributeRejectedWhileScanMatchesNothing.cs` |
| `dynamodb.ddbgsi-scan-consistent-read-on-an-index-rejected-while-base-scan-succeeds` | `4df2823` | ported | `DynamoDb/DdbgsiScanConsistentReadOnAnIndexRejectedWhileBaseScanSucceeds.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-condition-on-non-schema-attribute-rejected` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecConditionOnNonSchemaAttributeRejected.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-consumed-capacity-reports-vector-bytes` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecConsumedCapacityReportsVectorBytes.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-delete-unknown-vector-index-rejected` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecDeleteUnknownVectorIndexRejected.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-distance-functions-invert-topk-selection` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecDistanceFunctionsInvertTopkSelection.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-hash-schema-search-without-condition-rejected` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecHashSchemaSearchWithoutConditionRejected.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-include-projection-hides-unprojected-attributes` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecIncludeProjectionHidesUnprojectedAttributes.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-index-tracks-update-and-delete-item` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecIndexTracksUpdateAndDeleteItem.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-inline-filter-range-comparator-rejected` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecInlineFilterRangeComparatorRejected.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-search-condition-filters-below-topk-with-projection` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecSearchConditionFiltersBelowTopkWithProjection.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-search-vector-dimension-mismatch-rejected` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecSearchVectorDimensionMismatchRejected.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-topk-above-100-rejected` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecTopkAbove100Rejected.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-two-index-updates-in-one-call-rejected` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecTwoIndexUpdatesInOneCallRejected.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-unknown-index-name-rejected` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecUnknownIndexNameRejected.cs` |
| `dynamodb.deep-dynamodb-1-ddbvec-update-table-backfills-preexisting-items` | `4df2823` | ported | `DynamoDb/DeepDynamodb1DdbvecUpdateTableBackfillsPreexistingItems.cs` |
| `dynamodb.deep-dynamodb-2-describe-item-count-lags-scan` | `4df2823` | ported | `DynamoDb/DeepDynamodb2DescribeItemCountLagsScan.cs` |
| `dynamodb.deep-dynamodb-2-list-append-operand-order` | `4df2823` | ported | `DynamoDb/DeepDynamodb2ListAppendOperandOrder.cs` |
| `dynamodb.deep-dynamodb-2-list-index-clauses-read-one-pre-image` | `4df2823` | ported | `DynamoDb/DeepDynamodb2ListIndexClausesReadOnePreImage.cs` |
| `dynamodb.deep-dynamodb-2-list-index-on-non-list-rejected` | `4df2823` | ported | `DynamoDb/DeepDynamodb2ListIndexOnNonListRejected.cs` |
| `dynamodb.deep-dynamodb-2-overlapping-document-paths-rejected` | `4df2823` | ported | `DynamoDb/DeepDynamodb2OverlappingDocumentPathsRejected.cs` |
| `dynamodb.deep-dynamodb-2-remove-list-index-out-of-bounds-is-noop` | `4df2823` | ported | `DynamoDb/DeepDynamodb2RemoveListIndexOutOfBoundsIsNoop.cs` |
| `dynamodb.deep-dynamodb-2-remove-list-indices-resolve-against-original` | `4df2823` | ported | `DynamoDb/DeepDynamodb2RemoveListIndicesResolveAgainstOriginal.cs` |
| `dynamodb.deep-dynamodb-2-same-list-index-in-two-clauses-rejected` | `4df2823` | ported | `DynamoDb/DeepDynamodb2SameListIndexInTwoClausesRejected.cs` |
| `dynamodb.deep-dynamodb-2-set-list-index-out-of-bounds-appends` | `4df2823` | ported | `DynamoDb/DeepDynamodb2SetListIndexOutOfBoundsAppends.cs` |
| `dynamodb.deep-dynamodb-2-set-list-index-replaces-in-place` | `4df2823` | ported | `DynamoDb/DeepDynamodb2SetListIndexReplacesInPlace.cs` |
| `dynamodb.deep-dynamodb-2-set-nested-path-on-missing-parent-rejected` | `4df2823` | ported | `DynamoDb/DeepDynamodb2SetNestedPathOnMissingParentRejected.cs` |
| `dynamodb.deep-dynamodb-2-shrinking-update-charges-larger-image` | `4df2823` | ported | `DynamoDb/DeepDynamodb2ShrinkingUpdateChargesLargerImage.cs` |
| `dynamodb.deep-dynamodb-2-update-clauses-read-one-pre-image` | `4df2823` | ported | `DynamoDb/DeepDynamodb2UpdateClausesReadOnePreImage.cs` |
| `dynamodb.deep-expression-attribute-value-in-condition-and-set-rejected-before-conditional-check` | `4df2823` | ported | `DynamoDb/DeepExpressionAttributeValueInConditionAndSetRejectedBeforeConditionalCheck.cs` |
| `dynamodb.delete-backup-in-use-during-restore-rejected` | `4df2823` | ported | `DynamoDb/DeleteBackupInUseDuringRestoreRejected.cs` |
| `dynamodb.delete-backup-not-found-rejected` | `4df2823` | ported | `DynamoDb/DeleteBackupNotFoundRejected.cs` |
| `dynamodb.delete-item` | `4df2823` | ported | `DynamoDb/DeleteItem.cs` |
| `dynamodb.delete-item-condition-between-bounds-validated` | `4df2823` | ported | `DynamoDb/DeleteItemConditionBetweenBoundsValidated.cs` |
| `dynamodb.delete-item-condition-expression-attribute-not-exists` | `4df2823` | ported | `DynamoDb/DeleteItemConditionExpressionAttributeNotExists.cs` |
| `dynamodb.delete-item-condition-expression-invalid-attribute-name-rejected` | `4df2823` | ported | `DynamoDb/DeleteItemConditionExpressionInvalidAttributeNameRejected.cs` |
| `dynamodb.delete-item-expected-and-conditionexpression-mutually-exclusive-rejected` | `4df2823` | ported | `DynamoDb/DeleteItemExpectedAndConditionexpressionMutuallyExclusiveRejected.cs` |
| `dynamodb.delete-item-idempotent-missing-key` | `4df2823` | ported | `DynamoDb/DeleteItemIdempotentMissingKey.cs` |
| `dynamodb.delete-item-key-duplicate-set-member-byte-order-rejected` | `4df2823` | ported | `DynamoDb/DeleteItemKeyDuplicateSetMemberByteOrderRejected.cs` |
| `dynamodb.delete-item-nonexistent-key-is-noop` | `4df2823` | ported | `DynamoDb/DeleteItemNonexistentKeyIsNoop.cs` |
| `dynamodb.delete-item-on-a-missing-item-still-consumes-a-write-unit` | `4df2823` | ported | `DynamoDb/DeleteItemOnAMissingItemStillConsumesAWriteUnit.cs` |
| `dynamodb.delete-item-write-capacity-billed-on-deleted-item-size` | `4df2823` | ported | `DynamoDb/DeleteItemWriteCapacityBilledOnDeletedItemSize.cs` |
| `dynamodb.delete-resource-policy-idempotent` | `4df2823` | ported | `DynamoDb/DeleteResourcePolicyIdempotent.cs` |
| `dynamodb.delete-resource-policy-nonexistent-idempotent-or-rejected` | `4df2823` | ported | `DynamoDb/DeleteResourcePolicyNonexistentIdempotentOrRejected.cs` |
| `dynamodb.delete-set-operand-type-mismatch-rejected` | `4df2823` | ported | `DynamoDb/DeleteSetOperandTypeMismatchRejected.cs` |
| `dynamodb.delete-table-source-region-within-24h-of-replica-add-rejected` | `4df2823` | ported | `DynamoDb/DeleteTableSourceRegionWithin24hOfReplicaAddRejected.cs` |
| `dynamodb.delete-table-twice-rejected-not-silently-succeeds` | `4df2823` | ported | `DynamoDb/DeleteTableTwiceRejectedNotSilentlySucceeds.cs` |
| `dynamodb.delete-table-with-active-gsi-reports-deleting` | `4df2823` | ported | `DynamoDb/DeleteTableWithActiveGsiReportsDeleting.cs` |
| `dynamodb.deletion-protection-blocks-delete` | `4df2823` | ported | `DynamoDb/DeletionProtectionBlocksDelete.cs` |
| `dynamodb.deletion-protection-permits-item-deletes-and-survives-a-stream-update` | `4df2823` | ported | `DynamoDb/DeletionProtectionPermitsItemDeletesAndSurvivesAStreamUpdate.cs` |
| `dynamodb.deletion-protection-turned-on-by-update-table-refuses-delete` | `4df2823` | ported | `DynamoDb/DeletionProtectionTurnedOnByUpdateTableRefusesDelete.cs` |
| `dynamodb.deploy-cfn-gsi-queryable-after-deploy` | `4df2823` | not ported | CloudFormation stacks |
| `dynamodb.deploy-cfn-stream-arn-captures-writes` | `4df2823` | not ported | CloudFormation stacks |
| `dynamodb.deploy-cfn-table-duplicate-name-create-rejected` | `4df2823` | not ported | CloudFormation stacks |
| `dynamodb.deploy-cfn-table-put-get-roundtrip` | `4df2823` | not ported | CloudFormation stacks |
| `dynamodb.deploy-cfn-ttl-attribute-expires-item` | `4df2823` | not ported | CloudFormation stacks |
| `dynamodb.deploy-dynamodb-1-cfn-global-table-bidirectional-then-replica-removal-rejected` | `4df2823` | not ported | CloudFormation stacks |
| `dynamodb.describe-backup-not-found-rejected` | `4df2823` | ported | `DynamoDb/DescribeBackupNotFoundRejected.cs` |
| `dynamodb.describe-contributor-insights-not-found-rejected` | `4df2823` | ported | `DynamoDb/DescribeContributorInsightsNotFoundRejected.cs` |
| `dynamodb.describe-contributor-insights-resource-not-found-rejected` | `4df2823` | ported | `DynamoDb/DescribeContributorInsightsResourceNotFoundRejected.cs` |
| `dynamodb.describe-endpoints` | `4df2823` | ported | `DynamoDb/DescribeEndpoints.cs` |
| `dynamodb.describe-endpoints-returns-server-endpoints` | `4df2823` | ported | `DynamoDb/DescribeEndpointsReturnsServerEndpoints.cs` |
| `dynamodb.describe-global-table-not-found-rejected` | `4df2823` | ported | `DynamoDb/DescribeGlobalTableNotFoundRejected.cs` |
| `dynamodb.describe-import-not-found-rejected` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.describe-kinesis-streaming-destination-resource-not-found-rejected` | `4df2823` | ported | `DynamoDb/DescribeKinesisStreamingDestinationResourceNotFoundRejected.cs` |
| `dynamodb.describe-limits` | `4df2823` | ported | `DynamoDb/DescribeLimits.cs` |
| `dynamodb.describe-limits-returns-account-capacity` | `4df2823` | ported | `DynamoDb/DescribeLimitsReturnsAccountCapacity.cs` |
| `dynamodb.describetable-authorization-header-and-x-amz-algorithm-query-conflict-rejected` | `4df2823` | ported | `DynamoDb/DescribetableAuthorizationHeaderAndXAmzAlgorithmQueryConflictRejected.cs` |
| `dynamodb.disable-kinesis-never-enabled` | `4df2823` | not ported | Kinesis streaming destinations |
| `dynamodb.disable-kinesis-streaming-destination-not-enabled-rejected` | `4df2823` | not ported | Kinesis streaming destinations |
| `dynamodb.disable-kinesis-streaming-destination-omits-configuration` | `4df2823` | not ported | Kinesis streaming destinations |
| `dynamodb.disabling-a-stream-that-does-not-exist-rejected` | `4df2823` | ported | `DynamoDb/DisablingAStreamThatDoesNotExistRejected.cs` |
| `dynamodb.document-nesting-depth-32-levels-accepted-33-rejected` | `4df2823` | ported | `DynamoDb/DocumentNestingDepth32LevelsAccepted33Rejected.cs` |
| `dynamodb.duplicate-set-members-in-condition-key-and-transact-rejected` | `4df2823` | ported | `DynamoDb/DuplicateSetMembersInConditionKeyAndTransactRejected.cs` |
| `dynamodb.e2e-event-sourcing` | `4df2823` | ported | `DynamoDb/E2eEventSourcing.cs` |
| `dynamodb.e2e-optimistic-locking` | `4df2823` | ported | `DynamoDb/E2eOptimisticLocking.cs` |
| `dynamodb.e2e-order-fulfillment-concurrent-stock-contention` | `4df2823` | ported | `DynamoDb/E2eOrderFulfillmentConcurrentStockContention.cs` |
| `dynamodb.e2e-order-fulfillment-conditional-workflow` | `4df2823` | ported | `DynamoDb/E2eOrderFulfillmentConditionalWorkflow.cs` |
| `dynamodb.e2e-paginated-export` | `4df2823` | ported | `DynamoDb/E2ePaginatedExport.cs` |
| `dynamodb.e2e-resource-policy-grant-revoke-access-flow` | `4df2823` | not ported | IAM roles, role assumption and Lambda |
| `dynamodb.e2e-shopping-cart-workflow` | `4df2823` | ported | `DynamoDb/E2eShoppingCartWorkflow.cs` |
| `dynamodb.empty-and-duplicate-set-members-rejected-for-ss-ns-and-bs` | `4df2823` | ported | `DynamoDb/EmptyAndDuplicateSetMembersRejectedForSsNsAndBs.cs` |
| `dynamodb.empty-string-and-binary-base-table-keys-rejected` | `4df2823` | ported | `DynamoDb/EmptyStringAndBinaryBaseTableKeysRejected.cs` |
| `dynamodb.empty-string-expression-rejected-across-operations` | `4df2823` | ported | `DynamoDb/EmptyStringExpressionRejectedAcrossOperations.cs` |
| `dynamodb.empty-value-filter-expression-and-legacy-scan-filter-selection` | `4df2823` | ported | `DynamoDb/EmptyValueFilterExpressionAndLegacyScanFilterSelection.cs` |
| `dynamodb.enable-kinesis-invalid-arn` | `4df2823` | not ported | Kinesis streaming destinations |
| `dynamodb.enable-kinesis-streaming-destination-already-active-rejected` | `4df2823` | not ported | Kinesis streaming destinations |
| `dynamodb.enabling-a-stream-that-already-exists-rejected` | `4df2823` | ported | `DynamoDb/EnablingAStreamThatAlreadyExistsRejected.cs` |
| `dynamodb.error-conditional-check-failed-shape` | `4df2823` | ported | `DynamoDb/ErrorConditionalCheckFailedShape.cs` |
| `dynamodb.error-query-without-key-condition` | `4df2823` | ported | `DynamoDb/ErrorQueryWithoutKeyCondition.cs` |
| `dynamodb.error-resource-not-found` | `4df2823` | ported | `DynamoDb/ErrorResourceNotFound.cs` |
| `dynamodb.error-table-already-exists` | `4df2823` | ported | `DynamoDb/ErrorTableAlreadyExists.cs` |
| `dynamodb.error-validation-empty-key` | `4df2823` | ported | `DynamoDb/ErrorValidationEmptyKey.cs` |
| `dynamodb.eventually-consistent-get-consumes-half-of-a-strong-get` | `4df2823` | ported | `DynamoDb/EventuallyConsistentGetConsumesHalfOfAStrongGet.cs` |
| `dynamodb.exclusive-start-key-outside-range-and-missing-attribute-rejected` | `4df2823` | ported | `DynamoDb/ExclusiveStartKeyOutsideRangeAndMissingAttributeRejected.cs` |
| `dynamodb.execute-statement-conditional-update-failure` | `4df2823` | ported | `DynamoDb/ExecuteStatementConditionalUpdateFailure.cs` |
| `dynamodb.execute-statement-extra-token-after-complete-statement-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementExtraTokenAfterCompleteStatementRejected.cs` |
| `dynamodb.execute-statement-function-requires-document-path-or-operand-count-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementFunctionRequiresDocumentPathOrOperandCountRejected.cs` |
| `dynamodb.execute-statement-gsi-consistent-read-rejected-while-lsi-consistent-read-succeeds` | `4df2823` | ported | `DynamoDb/ExecuteStatementGsiConsistentReadRejectedWhileLsiConsistentReadSucceeds.cs` |
| `dynamodb.execute-statement-index-select-paginated-token-rejected-on-other-index` | `4df2823` | ported | `DynamoDb/ExecuteStatementIndexSelectPaginatedTokenRejectedOnOtherIndex.cs` |
| `dynamodb.execute-statement-invalid-partiql-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementInvalidPartiqlRejected.cs` |
| `dynamodb.execute-statement-limit-clause-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementLimitClauseRejected.cs` |
| `dynamodb.execute-statement-literal-leading-dot-with-no-mantissa-digit-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementLiteralLeadingDotWithNoMantissaDigitRejected.cs` |
| `dynamodb.execute-statement-malformed-literal-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementMalformedLiteralRejected.cs` |
| `dynamodb.execute-statement-missing-clause-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementMissingClauseRejected.cs` |
| `dynamodb.execute-statement-missing-literal-comparison-operators-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementMissingLiteralComparisonOperatorsRejected.cs` |
| `dynamodb.execute-statement-number-literal-exponent-and-leading-dot-forms` | `4df2823` | ported | `DynamoDb/ExecuteStatementNumberLiteralExponentAndLeadingDotForms.cs` |
| `dynamodb.execute-statement-number-precision-in-update-set-and-key-equality` | `4df2823` | ported | `DynamoDb/ExecuteStatementNumberPrecisionInUpdateSetAndKeyEquality.cs` |
| `dynamodb.execute-statement-ordering-comparison-rejects-parameterized-boolean-operand` | `4df2823` | ported | `DynamoDb/ExecuteStatementOrderingComparisonRejectsParameterizedBooleanOperand.cs` |
| `dynamodb.execute-statement-parameter-count-and-key-type-mismatch-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementParameterCountAndKeyTypeMismatchRejected.cs` |
| `dynamodb.execute-statement-quoting-rules` | `4df2823` | ported | `DynamoDb/ExecuteStatementQuotingRules.cs` |
| `dynamodb.execute-statement-rejects-foreign-next-token` | `4df2823` | ported | `DynamoDb/ExecuteStatementRejectsForeignNextToken.cs` |
| `dynamodb.execute-statement-rejects-non-numeric-n-parameter` | `4df2823` | ported | `DynamoDb/ExecuteStatementRejectsNonNumericNParameter.cs` |
| `dynamodb.execute-statement-rejects-out-of-range-n-parameter` | `4df2823` | ported | `DynamoDb/ExecuteStatementRejectsOutOfRangeNParameter.cs` |
| `dynamodb.execute-statement-select-multi-attribute-gsi-key-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementSelectMultiAttributeGsiKeyRejected.cs` |
| `dynamodb.execute-statement-select-through-gsi-without-equality-scans-and-filters` | `4df2823` | ported | `DynamoDb/ExecuteStatementSelectThroughGsiWithoutEqualityScansAndFilters.cs` |
| `dynamodb.execute-statement-undefined-function-and-projection-function-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementUndefinedFunctionAndProjectionFunctionRejected.cs` |
| `dynamodb.execute-statement-update-condition-failure-returns-old-item-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementUpdateConditionFailureReturnsOldItemRejected.cs` |
| `dynamodb.execute-statement-update-missing-sort-key-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementUpdateMissingSortKeyRejected.cs` |
| `dynamodb.execute-statement-vector-index-qualified-select-rejected` | `4df2823` | ported | `DynamoDb/ExecuteStatementVectorIndexQualifiedSelectRejected.cs` |
| `dynamodb.execute-transaction-client-token-idempotent-mismatch-rejected` | `4df2823` | ported | `DynamoDb/ExecuteTransactionClientTokenIdempotentMismatchRejected.cs` |
| `dynamodb.execute-transaction-condition-failure-cancels-and-bills-nothing` | `4df2823` | ported | `DynamoDb/ExecuteTransactionConditionFailureCancelsAndBillsNothing.cs` |
| `dynamodb.execute-transaction-duplicate-insert-rolls-back-sibling-insert` | `4df2823` | ported | `DynamoDb/ExecuteTransactionDuplicateInsertRollsBackSiblingInsert.cs` |
| `dynamodb.execute-transaction-exceeds-100-statements-rejected` | `4df2823` | ported | `DynamoDb/ExecuteTransactionExceeds100StatementsRejected.cs` |
| `dynamodb.execute-transaction-exists-condition-statement-commits-or-cancels` | `4df2823` | ported | `DynamoDb/ExecuteTransactionExistsConditionStatementCommitsOrCancels.cs` |
| `dynamodb.execute-transaction-idempotency` | `4df2823` | ported | `DynamoDb/ExecuteTransactionIdempotency.cs` |
| `dynamodb.execute-transaction-index-select-top-level-validation-not-cancellation` | `4df2823` | ported | `DynamoDb/ExecuteTransactionIndexSelectTopLevelValidationNotCancellation.cs` |
| `dynamodb.execute-transaction-malformed-statement-rejects-whole-call` | `4df2823` | ported | `DynamoDb/ExecuteTransactionMalformedStatementRejectsWholeCall.cs` |
| `dynamodb.execute-transaction-mixed-read-and-write-statements-rejected` | `4df2823` | ported | `DynamoDb/ExecuteTransactionMixedReadAndWriteStatementsRejected.cs` |
| `dynamodb.execute-transaction-multi-table-partiql-atomicity` | `4df2823` | ported | `DynamoDb/ExecuteTransactionMultiTablePartiqlAtomicity.cs` |
| `dynamodb.execute-transaction-read-response-shapes-and-write-consumed-capacity` | `4df2823` | ported | `DynamoDb/ExecuteTransactionReadResponseShapesAndWriteConsumedCapacity.cs` |
| `dynamodb.execute-transaction-read-responses-three-way-shape-and-write-consumed-capacity` | `4df2823` | ported | `DynamoDb/ExecuteTransactionReadResponsesThreeWayShapeAndWriteConsumedCapacity.cs` |
| `dynamodb.execute-transaction-replay-bills-exactly-wcu-then-rcu` | `4df2823` | ported | `DynamoDb/ExecuteTransactionReplayBillsExactlyWcuThenRcu.cs` |
| `dynamodb.execute-transaction-set-add-commits` | `4df2823` | ported | `DynamoDb/ExecuteTransactionSetAddCommits.cs` |
| `dynamodb.export-client-token-conflict` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.export-conflict-duplicate-rejected` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.export-invalid-export-time-rejected` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.export-invalid-time` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.export-lifecycle` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.export-table-lifecycle-to-completed` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.export-to-point-in-time-invalid-time-rejected` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.expression-attribute-keys-without-prefix-rejected-on-item-ops` | `4df2823` | ported | `DynamoDb/ExpressionAttributeKeysWithoutPrefixRejectedOnItemOps.cs` |
| `dynamodb.expression-attribute-map-key-syntax-and-length-rejected` | `4df2823` | ported | `DynamoDb/ExpressionAttributeMapKeySyntaxAndLengthRejected.cs` |
| `dynamodb.expression-attribute-names` | `4df2823` | ported | `DynamoDb/ExpressionAttributeNames.cs` |
| `dynamodb.expression-attribute-placeholders-without-an-expression-rejected` | `4df2823` | ported | `DynamoDb/ExpressionAttributePlaceholdersWithoutAnExpressionRejected.cs` |
| `dynamodb.filter-and-key-condition-expression-size-limit-rejected` | `4df2823` | ported | `DynamoDb/FilterAndKeyConditionExpressionSizeLimitRejected.cs` |
| `dynamodb.filter-expression-operator-limit-in-counts-as-one` | `4df2823` | ported | `DynamoDb/FilterExpressionOperatorLimitInCountsAsOne.cs` |
| `dynamodb.get-item-and-query-return-consumed-capacity-invalid-enum-rejected` | `4df2823` | ported | `DynamoDb/GetItemAndQueryReturnConsumedCapacityInvalidEnumRejected.cs` |
| `dynamodb.get-item-attributes-to-get-omits-a-missing-attribute` | `4df2823` | ported | `DynamoDb/GetItemAttributesToGetOmitsAMissingAttribute.cs` |
| `dynamodb.get-item-key-with-attribute-outside-schema-rejected` | `4df2823` | ported | `DynamoDb/GetItemKeyWithAttributeOutsideSchemaRejected.cs` |
| `dynamodb.get-item-missing-range-key-rejected` | `4df2823` | ported | `DynamoDb/GetItemMissingRangeKeyRejected.cs` |
| `dynamodb.get-item-projection-expression-alias-substitution-exceeds-4kb-accepted` | `4df2823` | ported | `DynamoDb/GetItemProjectionExpressionAliasSubstitutionExceeds4kbAccepted.cs` |
| `dynamodb.get-item-query-unaliased-dotted-name-is-a-nested-path` | `4df2823` | ported | `DynamoDb/GetItemQueryUnaliasedDottedNameIsANestedPath.cs` |
| `dynamodb.get-item-unaliased-reserved-word-in-a-projection-path-segment-rejected` | `4df2823` | ported | `DynamoDb/GetItemUnaliasedReservedWordInAProjectionPathSegmentRejected.cs` |
| `dynamodb.get-nonexistent-item-returns-empty` | `4df2823` | ported | `DynamoDb/GetNonexistentItemReturnsEmpty.cs` |
| `dynamodb.get-resource-policy-malformed-arn-rejected` | `4df2823` | ported | `DynamoDb/GetResourcePolicyMalformedArnRejected.cs` |
| `dynamodb.get-resource-policy-not-found-rejected` | `4df2823` | ported | `DynamoDb/GetResourcePolicyNotFoundRejected.cs` |
| `dynamodb.global-table-already-exists` | `4df2823` | ported | `DynamoDb/GlobalTableAlreadyExists.cs` |
| `dynamodb.global-table-create-describe-update-list` | `4df2823` | ported | `DynamoDb/GlobalTableCreateDescribeUpdateList.cs` |
| `dynamodb.global-table-lifecycle` | `4df2823` | ported | `DynamoDb/GlobalTableLifecycle.cs` |
| `dynamodb.global-table-lifecycle-create-describe-update` | `4df2823` | ported | `DynamoDb/GlobalTableLifecycleCreateDescribeUpdate.cs` |
| `dynamodb.global-table-mrsc-strong-consistency-and-conditional-write-conflict` | `4df2823` | ported | `DynamoDb/GlobalTableMrscStrongConsistencyAndConditionalWriteConflict.cs` |
| `dynamodb.global-table-mrsc-unsupported-configuration-rejected` | `4df2823` | ported | `DynamoDb/GlobalTableMrscUnsupportedConfigurationRejected.cs` |
| `dynamodb.global-table-not-found` | `4df2823` | ported | `DynamoDb/GlobalTableNotFound.cs` |
| `dynamodb.global-table-replica-already-exists-rejected` | `4df2823` | ported | `DynamoDb/GlobalTableReplicaAlreadyExistsRejected.cs` |
| `dynamodb.global-table-replica-item-replication-round-trip` | `4df2823` | ported | `DynamoDb/GlobalTableReplicaItemReplicationRoundTrip.cs` |
| `dynamodb.global-table-settings-describe-update` | `4df2823` | not ported | Application Auto Scaling |
| `dynamodb.global-table-settings-describe-update-roundtrip` | `4df2823` | not ported | Application Auto Scaling |
| `dynamodb.global-table-settings-index-not-found` | `4df2823` | ported | `DynamoDb/GlobalTableSettingsIndexNotFound.cs` |
| `dynamodb.global-table-settings-update` | `4df2823` | ported | `DynamoDb/GlobalTableSettingsUpdate.cs` |
| `dynamodb.gsi-add-and-remove-online` | `4df2823` | ported | `DynamoDb/GsiAddAndRemoveOnline.cs` |
| `dynamodb.gsi-added-online-indexes-items-written-while-it-builds` | `4df2823` | ported | `DynamoDb/GsiAddedOnlineIndexesItemsWrittenWhileItBuilds.cs` |
| `dynamodb.gsi-added-to-a-populated-table-backfills-every-preexisting-item` | `4df2823` | ported | `DynamoDb/GsiAddedToAPopulatedTableBackfillsEveryPreexistingItem.cs` |
| `dynamodb.gsi-added-to-a-populated-table-excludes-a-type-mismatched-item` | `4df2823` | ported | `DynamoDb/GsiAddedToAPopulatedTableExcludesATypeMismatchedItem.cs` |
| `dynamodb.gsi-consistent-read-rejected-while-lsi-consistent-read-succeeds` | `4df2823` | ported | `DynamoDb/GsiConsistentReadRejectedWhileLsiConsistentReadSucceeds.cs` |
| `dynamodb.gsi-creating-blocks-delete-table` | `4df2823` | ported | `DynamoDb/GsiCreatingBlocksDeleteTable.cs` |
| `dynamodb.gsi-creating-blocks-other-index-but-accepts-self-delete` | `4df2823` | ported | `DynamoDb/GsiCreatingBlocksOtherIndexButAcceptsSelfDelete.cs` |
| `dynamodb.gsi-delete-prunes-unshared-attribute-definitions-while-lsi-survives` | `4df2823` | ported | `DynamoDb/GsiDeletePrunesUnsharedAttributeDefinitionsWhileLsiSurvives.cs` |
| `dynamodb.gsi-duplicate-composite-keys-do-not-collapse` | `4df2823` | ported | `DynamoDb/GsiDuplicateCompositeKeysDoNotCollapse.cs` |
| `dynamodb.gsi-four-hash-attribute-key-query-order-free-and-tracks-moves-and-deletes` | `4df2823` | ported | `DynamoDb/GsiFourHashAttributeKeyQueryOrderFreeAndTracksMovesAndDeletes.cs` |
| `dynamodb.gsi-independent-throughput-scaling` | `4df2823` | ported | `DynamoDb/GsiIndependentThroughputScaling.cs` |
| `dynamodb.gsi-key-attribute-must-be-scalar` | `4df2823` | ported | `DynamoDb/GsiKeyAttributeMustBeScalar.cs` |
| `dynamodb.gsi-key-attribute-update-moves-the-index-entry` | `4df2823` | ported | `DynamoDb/GsiKeyAttributeUpdateMovesTheIndexEntry.cs` |
| `dynamodb.gsi-multi-attribute-hash-key-query-without-every-attribute-rejected` | `4df2823` | ported | `DynamoDb/GsiMultiAttributeHashKeyQueryWithoutEveryAttributeRejected.cs` |
| `dynamodb.gsi-projection-include` | `4df2823` | ported | `DynamoDb/GsiProjectionInclude.cs` |
| `dynamodb.gsi-projection-keys-only` | `4df2823` | ported | `DynamoDb/GsiProjectionKeysOnly.cs` |
| `dynamodb.gsi-query` | `4df2823` | ported | `DynamoDb/GsiQuery.cs` |
| `dynamodb.gsi-query-attributes-to-get-validation` | `4df2823` | ported | `DynamoDb/GsiQueryAttributesToGetValidation.cs` |
| `dynamodb.gsi-query-consistent-read-rejected` | `4df2823` | ported | `DynamoDb/GsiQueryConsistentReadRejected.cs` |
| `dynamodb.gsi-query-consumes-half-a-read-unit` | `4df2823` | ported | `DynamoDb/GsiQueryConsumesHalfAReadUnit.cs` |
| `dynamodb.gsi-query-exclusive-start-key-missing-an-index-key-attribute-rejected` | `4df2823` | ported | `DynamoDb/GsiQueryExclusiveStartKeyMissingAnIndexKeyAttributeRejected.cs` |
| `dynamodb.gsi-query-last-evaluated-key-carries-every-table-and-index-key-attribute` | `4df2823` | ported | `DynamoDb/GsiQueryLastEvaluatedKeyCarriesEveryTableAndIndexKeyAttribute.cs` |
| `dynamodb.gsi-query-omits-attributes-the-projection-excludes` | `4df2823` | ported | `DynamoDb/GsiQueryOmitsAttributesTheProjectionExcludes.cs` |
| `dynamodb.gsi-query-paginates-with-last-evaluated-key` | `4df2823` | ported | `DynamoDb/GsiQueryPaginatesWithLastEvaluatedKey.cs` |
| `dynamodb.gsi-query-paging-over-tied-index-keys-matches-unpaged-read` | `4df2823` | ported | `DynamoDb/GsiQueryPagingOverTiedIndexKeysMatchesUnpagedRead.cs` |
| `dynamodb.gsi-read-capacity-is-sized-by-the-projection-not-the-base-item` | `4df2823` | ported | `DynamoDb/GsiReadCapacityIsSizedByTheProjectionNotTheBaseItem.cs` |
| `dynamodb.gsi-scan-all-projection-and-filtered-limit-paging` | `4df2823` | ported | `DynamoDb/GsiScanAllProjectionAndFilteredLimitPaging.cs` |
| `dynamodb.gsi-scan-filter-expression-parenthesized-or-under-and` | `4df2823` | ported | `DynamoDb/GsiScanFilterExpressionParenthesizedOrUnderAnd.cs` |
| `dynamodb.gsi-scan-pagination-over-tied-index-sort-key` | `4df2823` | ported | `DynamoDb/GsiScanPaginationOverTiedIndexSortKey.cs` |
| `dynamodb.gsi-scan-unused-filter-expression-value-rejected` | `4df2823` | ported | `DynamoDb/GsiScanUnusedFilterExpressionValueRejected.cs` |
| `dynamodb.hot-partition-key-throttled-while-distributed-keys-at-same-rate-rejected` | `4df2823` | known gap: DynamoDbThroughput | `DynamoDb/HotPartitionKeyThrottledWhileDistributedKeysAtSameRateRejected.cs` |
| `dynamodb.import-not-found` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.import-table-conflict-rejected` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.import-table-describe-list-workflow` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.import-table-lifecycle` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.import-table-lifecycle-to-completed` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.import-table-malformed-item-reports-exact-failure` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.import-target-in-use` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.include-projection-governs-index-reads-on-lsi-and-gsi` | `4df2823` | ported | `DynamoDb/IncludeProjectionGovernsIndexReadsOnLsiAndGsi.cs` |
| `dynamodb.incomplete-sigv4-signature-lists-missing-components` | `4df2823` | known gap: DynamoDbAuthentication | `DynamoDb/IncompleteSigv4SignatureListsMissingComponents.cs` |
| `dynamodb.index-projection-attribute-budget-counts-a-name-once-per-index` | `4df2823` | ported | `DynamoDb/IndexProjectionAttributeBudgetCountsANameOncePerIndex.cs` |
| `dynamodb.item-collection-metrics-reported-only-for-lsi-tables` | `4df2823` | ported | `DynamoDb/ItemCollectionMetricsReportedOnlyForLsiTables.cs` |
| `dynamodb.item-collection-metrics-scoped-to-lsi-tables-and-requests` | `4df2823` | ported | `DynamoDb/ItemCollectionMetricsScopedToLsiTablesAndRequests.cs` |
| `dynamodb.item-size-400kb-limit` | `4df2823` | ported | `DynamoDb/ItemSize400kbLimit.cs` |
| `dynamodb.item-size-boundary-across-multiple-long-attribute-names` | `4df2823` | ported | `DynamoDb/ItemSizeBoundaryAcrossMultipleLongAttributeNames.cs` |
| `dynamodb.item-size-limit-counts-list-element-overhead` | `4df2823` | ported | `DynamoDb/ItemSizeLimitCountsListElementOverhead.cs` |
| `dynamodb.item-size-limit-enforced-at-400kb` | `4df2823` | ported | `DynamoDb/ItemSizeLimitEnforcedAt400kb.cs` |
| `dynamodb.item-size-limit-is-aggregate-not-per-attribute-rejected` | `4df2823` | ported | `DynamoDb/ItemSizeLimitIsAggregateNotPerAttributeRejected.cs` |
| `dynamodb.key-condition-illegal-sort-key-operator-rejected` | `4df2823` | ported | `DynamoDb/KeyConditionIllegalSortKeyOperatorRejected.cs` |
| `dynamodb.key-size-limits-on-batch-get-delete-update-and-binary-keys` | `4df2823` | ported | `DynamoDb/KeySizeLimitsOnBatchGetDeleteUpdateAndBinaryKeys.cs` |
| `dynamodb.keys-only-and-all-projections-are-exempt-from-the-attribute-budget` | `4df2823` | ported | `DynamoDb/KeysOnlyAndAllProjectionsAreExemptFromTheAttributeBudget.cs` |
| `dynamodb.keys-only-gsi-query-and-scan-page-by-projected-entry-size-not-base-item-size` | `4df2823` | ported | `DynamoDb/KeysOnlyGsiQueryAndScanPageByProjectedEntrySizeNotBaseItemSize.cs` |
| `dynamodb.keys-only-gsi-string-sort-key-pages-in-byte-order` | `4df2823` | ported | `DynamoDb/KeysOnlyGsiStringSortKeyPagesInByteOrder.cs` |
| `dynamodb.kinesis-destination-not-found` | `4df2823` | ported | `DynamoDb/KinesisDestinationNotFound.cs` |
| `dynamodb.kinesis-streaming-destination-enable-disable-lifecycle` | `4df2823` | not ported | Kinesis streaming destinations |
| `dynamodb.kinesis-streaming-destination-lifecycle` | `4df2823` | not ported | Kinesis streaming destinations |
| `dynamodb.kinesis-streaming-lifecycle` | `4df2823` | not ported | Kinesis streaming destinations |
| `dynamodb.legacy-attribute-updates-dotted-name-is-literal` | `4df2823` | ported | `DynamoDb/LegacyAttributeUpdatesDottedNameIsLiteral.cs` |
| `dynamodb.legacy-attributes-to-get-duplicate-name-rejected` | `4df2823` | ported | `DynamoDb/LegacyAttributesToGetDuplicateNameRejected.cs` |
| `dynamodb.legacy-attributes-to-get-empty-item-and-whole-nested-values` | `4df2823` | ported | `DynamoDb/LegacyAttributesToGetEmptyItemAndWholeNestedValues.cs` |
| `dynamodb.legacy-expected-and-default-or-explicit-put-item-ge-exists-false` | `4df2823` | ported | `DynamoDb/LegacyExpectedAndDefaultOrExplicitPutItemGeExistsFalse.cs` |
| `dynamodb.legacy-expected-and-default-or-explicit-update-delete-ge-exists-false` | `4df2823` | ported | `DynamoDb/LegacyExpectedAndDefaultOrExplicitUpdateDeleteGeExistsFalse.cs` |
| `dynamodb.legacy-expected-comparator-type-mismatch-on-put` | `4df2823` | ported | `DynamoDb/LegacyExpectedComparatorTypeMismatchOnPut.cs` |
| `dynamodb.legacy-expected-contains-in-null-comparator-matrix` | `4df2823` | ported | `DynamoDb/LegacyExpectedContainsInNullComparatorMatrix.cs` |
| `dynamodb.legacy-expected-exists-value-comparator-matrix` | `4df2823` | ported | `DynamoDb/LegacyExpectedExistsValueComparatorMatrix.cs` |
| `dynamodb.legacy-expected-list-map-value-equality` | `4df2823` | ported | `DynamoDb/LegacyExpectedListMapValueEquality.cs` |
| `dynamodb.legacy-expected-scalar-comparator-matrix-delete-item` | `4df2823` | ported | `DynamoDb/LegacyExpectedScalarComparatorMatrixDeleteItem.cs` |
| `dynamodb.legacy-expected-scalar-comparator-matrix-put-item` | `4df2823` | ported | `DynamoDb/LegacyExpectedScalarComparatorMatrixPutItem.cs` |
| `dynamodb.legacy-expected-scalar-comparator-matrix-update-item` | `4df2823` | ported | `DynamoDb/LegacyExpectedScalarComparatorMatrixUpdateItem.cs` |
| `dynamodb.legacy-keyconditions-disallowed-sort-key-operator-rejected` | `4df2823` | ported | `DynamoDb/LegacyKeyconditionsDisallowedSortKeyOperatorRejected.cs` |
| `dynamodb.legacy-keyconditions-sort-key-operators-select-and-order-items` | `4df2823` | ported | `DynamoDb/LegacyKeyconditionsSortKeyOperatorsSelectAndOrderItems.cs` |
| `dynamodb.legacy-queryfilter-list-map-value-equality` | `4df2823` | ported | `DynamoDb/LegacyQueryfilterListMapValueEquality.cs` |
| `dynamodb.legacy-queryfilter-scanfilter-keyconditions-argument-rules` | `4df2823` | ported | `DynamoDb/LegacyQueryfilterScanfilterKeyconditionsArgumentRules.cs` |
| `dynamodb.legacy-scan-query-filter-comparator-type-mismatch` | `4df2823` | ported | `DynamoDb/LegacyScanQueryFilterComparatorTypeMismatch.cs` |
| `dynamodb.list-append-operand-order-prepends-or-appends` | `4df2823` | ported | `DynamoDb/ListAppendOperandOrderPrependsOrAppends.cs` |
| `dynamodb.list-append-wrong-case-function-name-rejected` | `4df2823` | ported | `DynamoDb/ListAppendWrongCaseFunctionNameRejected.cs` |
| `dynamodb.list-backups-reflects-lifecycle` | `4df2823` | ported | `DynamoDb/ListBackupsReflectsLifecycle.cs` |
| `dynamodb.list-exports-pagination-consistent-order` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.list-global-tables-reflects-created-tables` | `4df2823` | ported | `DynamoDb/ListGlobalTablesReflectsCreatedTables.cs` |
| `dynamodb.list-imports-and-exports-empty-for-new-table` | `4df2823` | ported | `DynamoDb/ListImportsAndExportsEmptyForNewTable.cs` |
| `dynamodb.list-imports-pagination-consistent-order` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.list-index-past-end-set-appends-and-remove-shifts` | `4df2823` | ported | `DynamoDb/ListIndexPastEndSetAppendsAndRemoveShifts.cs` |
| `dynamodb.list-tables-pages-names-in-byte-order-across-punctuation` | `4df2823` | ported | `DynamoDb/ListTablesPagesNamesInByteOrderAcrossPunctuation.cs` |
| `dynamodb.lsi-consistent-query-pages-with-filter-counts-per-page` | `4df2823` | ported | `DynamoDb/LsiConsistentQueryPagesWithFilterCountsPerPage.cs` |
| `dynamodb.lsi-consistent-read` | `4df2823` | ported | `DynamoDb/LsiConsistentRead.cs` |
| `dynamodb.lsi-keys-only-projection-omits-attributes` | `4df2823` | ported | `DynamoDb/LsiKeysOnlyProjectionOmitsAttributes.cs` |
| `dynamodb.lsi-non-projected-query-charges-base-item-on-top-of-the-keys-only-index-read` | `4df2823` | ported | `DynamoDb/LsiNonProjectedQueryChargesBaseItemOnTopOfTheKeysOnlyIndexRead.cs` |
| `dynamodb.lsi-projection-include-echoes-non-key-attributes` | `4df2823` | ported | `DynamoDb/LsiProjectionIncludeEchoesNonKeyAttributes.cs` |
| `dynamodb.lsi-query` | `4df2823` | ported | `DynamoDb/LsiQuery.cs` |
| `dynamodb.lsi-query-pagination-exclusive-start-key` | `4df2823` | ported | `DynamoDb/LsiQueryPaginationExclusiveStartKey.cs` |
| `dynamodb.lsi-query-pagination-over-tied-index-sort-key` | `4df2823` | ported | `DynamoDb/LsiQueryPaginationOverTiedIndexSortKey.cs` |
| `dynamodb.lsi-query-sort-key-comparison-and-prefix-operators` | `4df2823` | ported | `DynamoDb/LsiQuerySortKeyComparisonAndPrefixOperators.cs` |
| `dynamodb.lsi-query-sort-key-operand-type-mismatch-rejected` | `4df2823` | ported | `DynamoDb/LsiQuerySortKeyOperandTypeMismatchRejected.cs` |
| `dynamodb.lsi-query-sort-key-value-type-mismatch-rejected` | `4df2823` | ported | `DynamoDb/LsiQuerySortKeyValueTypeMismatchRejected.cs` |
| `dynamodb.lsi-query-supports-consistent-read` | `4df2823` | ported | `DynamoDb/LsiQuerySupportsConsistentRead.cs` |
| `dynamodb.lsi-scan-all-and-include-projections-with-consistent-read` | `4df2823` | ported | `DynamoDb/LsiScanAllAndIncludeProjectionsWithConsistentRead.cs` |
| `dynamodb.lsi-scan-pagination-exclusive-start-key` | `4df2823` | ported | `DynamoDb/LsiScanPaginationExclusiveStartKey.cs` |
| `dynamodb.lsi-scan-pagination-over-tied-index-sort-key` | `4df2823` | ported | `DynamoDb/LsiScanPaginationOverTiedIndexSortKey.cs` |
| `dynamodb.lsi-sparse-item-omitted-from-index-query-and-scan` | `4df2823` | ported | `DynamoDb/LsiSparseItemOmittedFromIndexQueryAndScan.cs` |
| `dynamodb.malformed-n-in-expression-value-rejected-before-evaluation` | `4df2823` | ported | `DynamoDb/MalformedNInExpressionValueRejectedBeforeEvaluation.cs` |
| `dynamodb.malformed-n-in-key-is-top-level-validation-not-cancellation` | `4df2823` | ported | `DynamoDb/MalformedNInKeyIsTopLevelValidationNotCancellation.cs` |
| `dynamodb.mid-flight-socket-kill-recovery-probe-proves-exactly-once` | `4df2823` | ported | `DynamoDb/MidFlightSocketKillRecoveryProbeProvesExactlyOnce.cs` |
| `dynamodb.multiple-simultaneous-constraint-violations-rejected` | `4df2823` | known gap: DynamoDbDetail | `DynamoDb/MultipleSimultaneousConstraintViolationsRejected.cs` |
| `dynamodb.nesting-depth-error-precedence-across-malformed-requests` | `4df2823` | ported | `DynamoDb/NestingDepthErrorPrecedenceAcrossMalformedRequests.cs` |
| `dynamodb.nesting-depth-limit-applies-to-attribute-updates-and-condition-only-values` | `4df2823` | ported | `DynamoDb/NestingDepthLimitAppliesToAttributeUpdatesAndConditionOnlyValues.cs` |
| `dynamodb.nesting-depth-limit-covers-transact-condition-check-and-legacy-expected-values` | `4df2823` | ported | `DynamoDb/NestingDepthLimitCoversTransactConditionCheckAndLegacyExpectedValues.cs` |
| `dynamodb.nesting-depth-limit-enforced-at-32-levels` | `4df2823` | ported | `DynamoDb/NestingDepthLimitEnforcedAt32Levels.cs` |
| `dynamodb.new-index-on-a-provisioned-table-requires-its-own-throughput` | `4df2823` | ported | `DynamoDb/NewIndexOnAProvisionedTableRequiresItsOwnThroughput.cs` |
| `dynamodb.null-false-batchget-mixed-projection-and-query-keyconditions-with-expressions` | `4df2823` | ported | `DynamoDb/NullFalseBatchgetMixedProjectionAndQueryKeyconditionsWithExpressions.cs` |
| `dynamodb.number-byte-cost-exact-at-400kb-boundary-put-item-and-batch-write-item` | `4df2823` | ported | `DynamoDb/NumberByteCostExactAt400kbBoundaryPutItemAndBatchWriteItem.cs` |
| `dynamodb.number-byte-cost-matches-documented-approximation` | `4df2823` | ported | `DynamoDb/NumberByteCostMatchesDocumentedApproximation.cs` |
| `dynamodb.number-canonicalization-boundaries` | `4df2823` | ported | `DynamoDb/NumberCanonicalizationBoundaries.cs` |
| `dynamodb.number-magnitude-floor-put-update-batchwrite` | `4df2823` | ported | `DynamoDb/NumberMagnitudeFloorPutUpdateBatchwrite.cs` |
| `dynamodb.number-precision-limited-to-38-digits` | `4df2823` | ported | `DynamoDb/NumberPrecisionLimitedTo38Digits.cs` |
| `dynamodb.number-precision-overflow-in-exponent-form-rejected` | `4df2823` | ported | `DynamoDb/NumberPrecisionOverflowInExponentFormRejected.cs` |
| `dynamodb.number-set-add-type-mismatch-rejected` | `4df2823` | ported | `DynamoDb/NumberSetAddTypeMismatchRejected.cs` |
| `dynamodb.numeric-key-is-canonicalized-on-every-path` | `4df2823` | ported | `DynamoDb/NumericKeyIsCanonicalizedOnEveryPath.cs` |
| `dynamodb.numeric-sort-key-orders-numerically` | `4df2823` | ported | `DynamoDb/NumericSortKeyOrdersNumerically.cs` |
| `dynamodb.on-demand-table-accepts-a-new-index-with-no-throughput` | `4df2823` | ported | `DynamoDb/OnDemandTableAcceptsANewIndexWithNoThroughput.cs` |
| `dynamodb.on-demand-throughput-round-trips-at-create-then-update-changes-both-ceilings` | `4df2823` | ported | `DynamoDb/OnDemandThroughputRoundTripsAtCreateThenUpdateChangesBothCeilings.cs` |
| `dynamodb.oversized-item-to-a-missing-table-not-found-on-put-rejected-on-batch-write` | `4df2823` | ported | `DynamoDb/OversizedItemToAMissingTableNotFoundOnPutRejectedOnBatchWrite.cs` |
| `dynamodb.parallel-scan-segment-co-locates-every-sort-key-under-one-partition-key` | `4df2823` | ported | `DynamoDb/ParallelScanSegmentCoLocatesEverySortKeyUnderOnePartitionKey.cs` |
| `dynamodb.parallel-scan-segments-partition-the-table-exactly-once` | `4df2823` | ported | `DynamoDb/ParallelScanSegmentsPartitionTheTableExactlyOnce.cs` |
| `dynamodb.partiql-and-classic-api-address-one-item-store` | `4df2823` | ported | `DynamoDb/PartiqlAndClassicApiAddressOneItemStore.cs` |
| `dynamodb.partiql-attribute-exists-splits-null-typed-from-missing` | `4df2823` | ported | `DynamoDb/PartiqlAttributeExistsSplitsNullTypedFromMissing.cs` |
| `dynamodb.partiql-contains-and-begins-with-select-exact-matching-set` | `4df2823` | ported | `DynamoDb/PartiqlContainsAndBeginsWithSelectExactMatchingSet.cs` |
| `dynamodb.partiql-delete-noop-on-absent-key-and-update-from-rejected` | `4df2823` | ported | `DynamoDb/PartiqlDeleteNoopOnAbsentKeyAndUpdateFromRejected.cs` |
| `dynamodb.partiql-delete-returning-all-new-rejected` | `4df2823` | ported | `DynamoDb/PartiqlDeleteReturningAllNewRejected.cs` |
| `dynamodb.partiql-equal-and-not-equal-over-list-map-set-and-mixed-type-in` | `4df2823` | ported | `DynamoDb/PartiqlEqualAndNotEqualOverListMapSetAndMixedTypeIn.cs` |
| `dynamodb.partiql-equal-matches-binary-set-and-map-operands-by-value` | `4df2823` | ported | `DynamoDb/PartiqlEqualMatchesBinarySetAndMapOperandsByValue.cs` |
| `dynamodb.partiql-from-clause-path-arity-rejected` | `4df2823` | ported | `DynamoDb/PartiqlFromClausePathArityRejected.cs` |
| `dynamodb.partiql-hash-and-unterminated-block-comment-rejected` | `4df2823` | ported | `DynamoDb/PartiqlHashAndUnterminatedBlockCommentRejected.cs` |
| `dynamodb.partiql-insert-and-update-item-size-boundary-at-409600-bytes` | `4df2823` | ported | `DynamoDb/PartiqlInsertAndUpdateItemSizeBoundaryAt409600Bytes.cs` |
| `dynamodb.partiql-line-and-block-comments-ignored-order-by-asc` | `4df2823` | ported | `DynamoDb/PartiqlLineAndBlockCommentsIgnoredOrderByAsc.cs` |
| `dynamodb.partiql-or-negated-sort-key-branch-runs-and-unions` | `4df2823` | ported | `DynamoDb/PartiqlOrNegatedSortKeyBranchRunsAndUnions.cs` |
| `dynamodb.partiql-ordering-binary-byte-order-and-string-number-cross-type-match` | `4df2823` | ported | `DynamoDb/PartiqlOrderingBinaryByteOrderAndStringNumberCrossTypeMatch.cs` |
| `dynamodb.partiql-partition-key-predicate-is-planned-as-a-query-not-a-scan` | `4df2823` | ported | `DynamoDb/PartiqlPartitionKeyPredicateIsPlannedAsAQueryNotAScan.cs` |
| `dynamodb.partiql-relational-operators-reject-complex-type-operands` | `4df2823` | ported | `DynamoDb/PartiqlRelationalOperatorsRejectComplexTypeOperands.cs` |
| `dynamodb.partiql-select-and-query-agree-on-one-partition` | `4df2823` | ported | `DynamoDb/PartiqlSelectAndQueryAgreeOnOnePartition.cs` |
| `dynamodb.partiql-select-between-in-or-order-by-desc-and-not-equal` | `4df2823` | ported | `DynamoDb/PartiqlSelectBetweenInOrOrderByDescAndNotEqual.cs` |
| `dynamodb.partiql-select-condition-function-forms` | `4df2823` | ported | `DynamoDb/PartiqlSelectConditionFunctionForms.cs` |
| `dynamodb.partiql-select-gsi-include-projection-nonprojected-filter-rejected` | `4df2823` | ported | `DynamoDb/PartiqlSelectGsiIncludeProjectionNonprojectedFilterRejected.cs` |
| `dynamodb.partiql-select-index-qualified-capacity-table-and-index-arms` | `4df2823` | ported | `DynamoDb/PartiqlSelectIndexQualifiedCapacityTableAndIndexArms.cs` |
| `dynamodb.partiql-select-index-qualifier-projection-follows-index-type` | `4df2823` | ported | `DynamoDb/PartiqlSelectIndexQualifierProjectionFollowsIndexType.cs` |
| `dynamodb.partiql-select-index-unprojected-filter-refusal-scoped-to-key-condition` | `4df2823` | ported | `DynamoDb/PartiqlSelectIndexUnprojectedFilterRefusalScopedToKeyCondition.cs` |
| `dynamodb.partiql-select-lsi-keys-only-nonprojected-filter-and-in-between-key-forms` | `4df2823` | ported | `DynamoDb/PartiqlSelectLsiKeysOnlyNonprojectedFilterAndInBetweenKeyForms.cs` |
| `dynamodb.partiql-select-projects-nested-map-and-list-paths` | `4df2823` | ported | `DynamoDb/PartiqlSelectProjectsNestedMapAndListPaths.cs` |
| `dynamodb.partiql-select-through-gsi-qualifier` | `4df2823` | ported | `DynamoDb/PartiqlSelectThroughGsiQualifier.cs` |
| `dynamodb.partiql-select-through-lsi-qualifier` | `4df2823` | ported | `DynamoDb/PartiqlSelectThroughLsiQualifier.cs` |
| `dynamodb.partiql-set-add-literal-ss-and-ns` | `4df2823` | ported | `DynamoDb/PartiqlSetAddLiteralSsAndNs.cs` |
| `dynamodb.partiql-set-add-on-absent-item-rejected` | `4df2823` | ported | `DynamoDb/PartiqlSetAddOnAbsentItemRejected.cs` |
| `dynamodb.partiql-set-add-set-delete-accepted-forms` | `4df2823` | ported | `DynamoDb/PartiqlSetAddSetDeleteAcceptedForms.cs` |
| `dynamodb.partiql-set-delete-and-parameter-sets` | `4df2823` | ported | `DynamoDb/PartiqlSetDeleteAndParameterSets.cs` |
| `dynamodb.partiql-single-quote-literal-escaping` | `4df2823` | ported | `DynamoDb/PartiqlSingleQuoteLiteralEscaping.cs` |
| `dynamodb.partiql-size-and-attribute-type-select-exact-sets-and-reject-incompatible-size` | `4df2823` | ported | `DynamoDb/PartiqlSizeAndAttributeTypeSelectExactSetsAndRejectIncompatibleSize.cs` |
| `dynamodb.partiql-true-false-null-literals-are-case-insensitive` | `4df2823` | ported | `DynamoDb/PartiqlTrueFalseNullLiteralsAreCaseInsensitive.cs` |
| `dynamodb.partiql-typed-item-insert-update-select-then-deleted-table-refuses-statements` | `4df2823` | ported | `DynamoDb/PartiqlTypedItemInsertUpdateSelectThenDeletedTableRefusesStatements.cs` |
| `dynamodb.partiql-unicode-string-round-trip` | `4df2823` | ported | `DynamoDb/PartiqlUnicodeStringRoundTrip.cs` |
| `dynamodb.partiql-unknown-index-qualifier-rejected` | `4df2823` | ported | `DynamoDb/PartiqlUnknownIndexQualifierRejected.cs` |
| `dynamodb.partiql-unquoted-non-literal-token-as-value-rejected` | `4df2823` | ported | `DynamoDb/PartiqlUnquotedNonLiteralTokenAsValueRejected.cs` |
| `dynamodb.partiql-update-and-delete-returning-images` | `4df2823` | ported | `DynamoDb/PartiqlUpdateAndDeleteReturningImages.cs` |
| `dynamodb.partiql-update-delete-attribute-exists-condition` | `4df2823` | ported | `DynamoDb/PartiqlUpdateDeleteAttributeExistsCondition.cs` |
| `dynamodb.partiql-update-from-first-clause-order-runs-as-write` | `4df2823` | ported | `DynamoDb/PartiqlUpdateFromFirstClauseOrderRunsAsWrite.cs` |
| `dynamodb.partiql-update-growth-past-400kb-matches-update-item` | `4df2823` | ported | `DynamoDb/PartiqlUpdateGrowthPast400kbMatchesUpdateItem.cs` |
| `dynamodb.partiql-update-modified-images-empty-for-absent-half` | `4df2823` | ported | `DynamoDb/PartiqlUpdateModifiedImagesEmptyForAbsentHalf.cs` |
| `dynamodb.partiql-update-negative-list-index-rejected` | `4df2823` | ported | `DynamoDb/PartiqlUpdateNegativeListIndexRejected.cs` |
| `dynamodb.partiql-update-returning-modified-images-on-nested-paths` | `4df2823` | ported | `DynamoDb/PartiqlUpdateReturningModifiedImagesOnNestedPaths.cs` |
| `dynamodb.partiql-update-returning-modified-images-over-list-indices` | `4df2823` | ported | `DynamoDb/PartiqlUpdateReturningModifiedImagesOverListIndices.cs` |
| `dynamodb.partiql-update-set-list-append-non-list-literal-operand-rejected` | `4df2823` | ported | `DynamoDb/PartiqlUpdateSetListAppendNonListLiteralOperandRejected.cs` |
| `dynamodb.partiql-update-set-literal-and-set-add-reject-mixed-type-set-members` | `4df2823` | ported | `DynamoDb/PartiqlUpdateSetLiteralAndSetAddRejectMixedTypeSetMembers.cs` |
| `dynamodb.partiql-update-set-remove-nested-map-and-list-paths` | `4df2823` | ported | `DynamoDb/PartiqlUpdateSetRemoveNestedMapAndListPaths.cs` |
| `dynamodb.partiql-update-set-subtraction-applies-and-multiply-divide-rejected` | `4df2823` | ported | `DynamoDb/PartiqlUpdateSetSubtractionAppliesAndMultiplyDivideRejected.cs` |
| `dynamodb.partiql-update-where-and-updateitem-condition-expression-agree-on-set-and-map-equality` | `4df2823` | ported | `DynamoDb/PartiqlUpdateWhereAndUpdateitemConditionExpressionAgreeOnSetAndMapEquality.cs` |
| `dynamodb.partiql-write-and-transaction-condition-function-ccf` | `4df2823` | ported | `DynamoDb/PartiqlWriteAndTransactionConditionFunctionCcf.cs` |
| `dynamodb.partiql-write-through-index-qualifier-rejected` | `4df2823` | ported | `DynamoDb/PartiqlWriteThroughIndexQualifierRejected.cs` |
| `dynamodb.plain-write-racing-a-transaction-rejected` | `4df2823` | ported | `DynamoDb/PlainWriteRacingATransactionRejected.cs` |
| `dynamodb.point-in-time-export-restore-workflow` | `4df2823` | not ported | S3 (exports and imports) |
| `dynamodb.projection-expression` | `4df2823` | ported | `DynamoDb/ProjectionExpression.cs` |
| `dynamodb.projection-expression-does-not-reduce-get-item-and-query-consumed-capacity` | `4df2823` | ported | `DynamoDb/ProjectionExpressionDoesNotReduceGetItemAndQueryConsumedCapacity.cs` |
| `dynamodb.projection-list-index-out-of-range-null-element-and-nested-list` | `4df2823` | ported | `DynamoDb/ProjectionListIndexOutOfRangeNullElementAndNestedList.cs` |
| `dynamodb.projection-list-indices-compact-in-index-order` | `4df2823` | ported | `DynamoDb/ProjectionListIndicesCompactInIndexOrder.cs` |
| `dynamodb.projection-negative-list-index-rejected-across-read-operations` | `4df2823` | ported | `DynamoDb/ProjectionNegativeListIndexRejectedAcrossReadOperations.cs` |
| `dynamodb.projection-nested-map-path-returns-leaf-under-parent` | `4df2823` | ported | `DynamoDb/ProjectionNestedMapPathReturnsLeafUnderParent.cs` |
| `dynamodb.projection-overlap-path-order-and-list-index-rendering-rejected` | `4df2823` | ported | `DynamoDb/ProjectionOverlapPathOrderAndListIndexRenderingRejected.cs` |
| `dynamodb.projection-overlap-rejected-on-query-batch-and-transact-get` | `4df2823` | ported | `DynamoDb/ProjectionOverlapRejectedOnQueryBatchAndTransactGet.cs` |
| `dynamodb.projection-overlapping-document-paths-rejected` | `4df2823` | ported | `DynamoDb/ProjectionOverlappingDocumentPathsRejected.cs` |
| `dynamodb.projection-shared-list-index-and-nested-map-merge-across-read-operations` | `4df2823` | ported | `DynamoDb/ProjectionSharedListIndexAndNestedMapMergeAcrossReadOperations.cs` |
| `dynamodb.projection-shared-list-index-merges-into-one-element` | `4df2823` | ported | `DynamoDb/ProjectionSharedListIndexMergesIntoOneElement.cs` |
| `dynamodb.projection-update-and-query-filter-expressions-accepted-at-4096-bytes` | `4df2823` | ported | `DynamoDb/ProjectionUpdateAndQueryFilterExpressionsAcceptedAt4096Bytes.cs` |
| `dynamodb.provisioned-table-rejects-a-switch-with-no-throughput` | `4df2823` | ported | `DynamoDb/ProvisionedTableRejectsASwitchWithNoThroughput.cs` |
| `dynamodb.provisioned-table-rejects-an-on-demand-throughput-ceiling` | `4df2823` | ported | `DynamoDb/ProvisionedTableRejectsAnOnDemandThroughputCeiling.cs` |
| `dynamodb.provisioned-throughput-exceeded-enforced-across-reads` | `4df2823` | known gap: DynamoDbThroughput | `DynamoDb/ProvisionedThroughputExceededEnforcedAcrossReads.cs` |
| `dynamodb.provisioned-throughput-on-pay-per-request` | `4df2823` | ported | `DynamoDb/ProvisionedThroughputOnPayPerRequest.cs` |
| `dynamodb.provisioned-write-throttle-reason-and-metric-agreement-rejected` | `4df2823` | not ported | CloudWatch metrics |
| `dynamodb.put-and-delete-item-reject-return-values-all-new` | `4df2823` | ported | `DynamoDb/PutAndDeleteItemRejectReturnValuesAllNew.cs` |
| `dynamodb.put-batch-write-update-index-key-empty-value-rejected` | `4df2823` | ported | `DynamoDb/PutBatchWriteUpdateIndexKeyEmptyValueRejected.cs` |
| `dynamodb.put-get-all-scalar-types` | `4df2823` | ported | `DynamoDb/PutGetAllScalarTypes.cs` |
| `dynamodb.put-get-binary-blob-round-trip` | `4df2823` | ported | `DynamoDb/PutGetBinaryBlobRoundTrip.cs` |
| `dynamodb.put-get-collection-types` | `4df2823` | ported | `DynamoDb/PutGetCollectionTypes.cs` |
| `dynamodb.put-get-empty-vs-absent-attribute-preserved` | `4df2823` | ported | `DynamoDb/PutGetEmptyVsAbsentAttributePreserved.cs` |
| `dynamodb.put-get-numeric-high-precision-round-trip` | `4df2823` | ported | `DynamoDb/PutGetNumericHighPrecisionRoundTrip.cs` |
| `dynamodb.put-get-roundtrip-string` | `4df2823` | ported | `DynamoDb/PutGetRoundtripString.cs` |
| `dynamodb.put-get-unicode-string-round-trip` | `4df2823` | ported | `DynamoDb/PutGetUnicodeStringRoundTrip.cs` |
| `dynamodb.put-item-and-batch-write-item-binary-item-size-boundary-raw-bytes` | `4df2823` | ported | `DynamoDb/PutItemAndBatchWriteItemBinaryItemSizeBoundaryRawBytes.cs` |
| `dynamodb.put-item-and-batch-write-item-item-size-boundary` | `4df2823` | ported | `DynamoDb/PutItemAndBatchWriteItemItemSizeBoundary.cs` |
| `dynamodb.put-item-and-batch-write-item-multibyte-string-size-utf8-bytes` | `4df2823` | ported | `DynamoDb/PutItemAndBatchWriteItemMultibyteStringSizeUtf8Bytes.cs` |
| `dynamodb.put-item-and-batch-write-item-size-limit-includes-all-projected-lsi-entry` | `4df2823` | ported | `DynamoDb/PutItemAndBatchWriteItemSizeLimitIncludesAllProjectedLsiEntry.cs` |
| `dynamodb.put-item-and-search-vectors-return-consumed-capacity-invalid-enum-rejected` | `4df2823` | ported | `DynamoDb/PutItemAndSearchVectorsReturnConsumedCapacityInvalidEnumRejected.cs` |
| `dynamodb.put-item-binary-set-zero-length-member-round-trips` | `4df2823` | ported | `DynamoDb/PutItemBinarySetZeroLengthMemberRoundTrips.cs` |
| `dynamodb.put-item-condition-nested-document-paths-and-functions-rejected` | `4df2823` | ported | `DynamoDb/PutItemConditionNestedDocumentPathsAndFunctionsRejected.cs` |
| `dynamodb.put-item-condition-nested-document-paths-and-functions-that-hold` | `4df2823` | ported | `DynamoDb/PutItemConditionNestedDocumentPathsAndFunctionsThatHold.cs` |
| `dynamodb.put-item-consumed-capacity-sums-table-and-index-arms` | `4df2823` | ported | `DynamoDb/PutItemConsumedCapacitySumsTableAndIndexArms.cs` |
| `dynamodb.put-item-default-response-omits-attributes` | `4df2823` | ported | `DynamoDb/PutItemDefaultResponseOmitsAttributes.cs` |
| `dynamodb.put-item-fractional-and-negative-hash-key-omits-consumed-capacity` | `4df2823` | ported | `DynamoDb/PutItemFractionalAndNegativeHashKeyOmitsConsumedCapacity.cs` |
| `dynamodb.put-item-gsi-key-type-mismatch-rejected` | `4df2823` | ported | `DynamoDb/PutItemGsiKeyTypeMismatchRejected.cs` |
| `dynamodb.put-item-key-type-mismatch-rejected` | `4df2823` | ported | `DynamoDb/PutItemKeyTypeMismatchRejected.cs` |
| `dynamodb.put-item-nested-map-set-and-number-list-contents-count-toward-item-size-limit` | `4df2823` | ported | `DynamoDb/PutItemNestedMapSetAndNumberListContentsCountTowardItemSizeLimit.cs` |
| `dynamodb.put-item-nested-set-in-list-or-map-empty-rejected-empty-string-member-round-trips` | `4df2823` | ported | `DynamoDb/PutItemNestedSetInListOrMapEmptyRejectedEmptyStringMemberRoundTrips.cs` |
| `dynamodb.put-item-number-precision-exceeds-limit-rejected` | `4df2823` | ported | `DynamoDb/PutItemNumberPrecisionExceedsLimitRejected.cs` |
| `dynamodb.put-item-omitting-a-gsi-key-is-a-sparse-write` | `4df2823` | ported | `DynamoDb/PutItemOmittingAGsiKeyIsASparseWrite.cs` |
| `dynamodb.put-item-on-deleting-table-rejected` | `4df2823` | ported | `DynamoDb/PutItemOnDeletingTableRejected.cs` |
| `dynamodb.put-item-oversized-overwrite-rejected-ignoring-existing-image` | `4df2823` | ported | `DynamoDb/PutItemOversizedOverwriteRejectedIgnoringExistingImage.cs` |
| `dynamodb.put-item-overwrite-consumed-capacity-follows-larger-item-image` | `4df2823` | ported | `DynamoDb/PutItemOverwriteConsumedCapacityFollowsLargerItemImage.cs` |
| `dynamodb.put-item-overwrite-replaces-item` | `4df2823` | ported | `DynamoDb/PutItemOverwriteReplacesItem.cs` |
| `dynamodb.put-item-sparse-write-charges-no-index-capacity` | `4df2823` | ported | `DynamoDb/PutItemSparseWriteChargesNoIndexCapacity.cs` |
| `dynamodb.put-item-two-empty-members-in-ss-and-bs-rejected-as-duplicate-collections` | `4df2823` | ported | `DynamoDb/PutItemTwoEmptyMembersInSsAndBsRejectedAsDuplicateCollections.cs` |
| `dynamodb.put-item-vector-hash-search-schema-missing-vs-empty-string` | `4df2823` | ported | `DynamoDb/PutItemVectorHashSearchSchemaMissingVsEmptyString.cs` |
| `dynamodb.put-item-vector-index-charged-only-when-projection-changes` | `4df2823` | ported | `DynamoDb/PutItemVectorIndexChargedOnlyWhenProjectionChanges.cs` |
| `dynamodb.put-item-vector-of-wrong-dimension-rejected` | `4df2823` | ported | `DynamoDb/PutItemVectorOfWrongDimensionRejected.cs` |
| `dynamodb.put-item-vector-write-validation-and-search-projection` | `4df2823` | ported | `DynamoDb/PutItemVectorWriteValidationAndSearchProjection.cs` |
| `dynamodb.put-resource-policy-oversized` | `4df2823` | ported | `DynamoDb/PutResourcePolicyOversized.cs` |
| `dynamodb.put-resource-policy-revision-id-conflict-rejected` | `4df2823` | ported | `DynamoDb/PutResourcePolicyRevisionIdConflictRejected.cs` |
| `dynamodb.put-return-values-all-old` | `4df2823` | ported | `DynamoDb/PutReturnValuesAllOld.cs` |
| `dynamodb.put-update-delete-item-return-consumed-capacity-invalid-enum-rejected` | `4df2823` | ported | `DynamoDb/PutUpdateDeleteItemReturnConsumedCapacityInvalidEnumRejected.cs` |
| `dynamodb.putitem-condition-comparison-on-missing-attribute` | `4df2823` | ported | `DynamoDb/PutitemConditionComparisonOnMissingAttribute.cs` |
| `dynamodb.putitem-condition-expression-and-or-not-precedence` | `4df2823` | ported | `DynamoDb/PutitemConditionExpressionAndOrNotPrecedence.cs` |
| `dynamodb.putitem-not-contains-condition-gates-the-write` | `4df2823` | ported | `DynamoDb/PutitemNotContainsConditionGatesTheWrite.cs` |
| `dynamodb.query-and-put-item-placeholder-and-reserved-word-validation` | `4df2823` | ported | `DynamoDb/QueryAndPutItemPlaceholderAndReservedWordValidation.cs` |
| `dynamodb.query-and-update-item-placeholders-without-an-expression-rejected` | `4df2823` | ported | `DynamoDb/QueryAndUpdateItemPlaceholdersWithoutAnExpressionRejected.cs` |
| `dynamodb.query-base-table-primary-key-in-filter-expression-rejected` | `4df2823` | ported | `DynamoDb/QueryBaseTablePrimaryKeyInFilterExpressionRejected.cs` |
| `dynamodb.query-begins-with` | `4df2823` | ported | `DynamoDb/QueryBeginsWith.cs` |
| `dynamodb.query-capacity-gsi-rejects-consistent-read-lsi-accepts-it` | `4df2823` | ported | `DynamoDb/QueryCapacityGsiRejectsConsistentReadLsiAcceptsIt.cs` |
| `dynamodb.query-filter-expression` | `4df2823` | ported | `DynamoDb/QueryFilterExpression.cs` |
| `dynamodb.query-filter-expression-between-reversed-bounds-rejected` | `4df2823` | ported | `DynamoDb/QueryFilterExpressionBetweenReversedBoundsRejected.cs` |
| `dynamodb.query-filter-expression-in-operator-with-101-operands-rejected` | `4df2823` | ported | `DynamoDb/QueryFilterExpressionInOperatorWith101OperandsRejected.cs` |
| `dynamodb.query-filter-expression-never-and-partly-matching-costs-nothing` | `4df2823` | ported | `DynamoDb/QueryFilterExpressionNeverAndPartlyMatchingCostsNothing.cs` |
| `dynamodb.query-filter-expression-operators-match-scan-twin-in-partition` | `4df2823` | ported | `DynamoDb/QueryFilterExpressionOperatorsMatchScanTwinInPartition.cs` |
| `dynamodb.query-filter-expression-parentheses-and-document-paths` | `4df2823` | ported | `DynamoDb/QueryFilterExpressionParenthesesAndDocumentPaths.cs` |
| `dynamodb.query-filter-expression-redundant-parentheses-rejected` | `4df2823` | ported | `DynamoDb/QueryFilterExpressionRedundantParenthesesRejected.cs` |
| `dynamodb.query-filter-expression-undefined-value-rejected-over-a-1mb-partition` | `4df2823` | ported | `DynamoDb/QueryFilterExpressionUndefinedValueRejectedOverA1mbPartition.cs` |
| `dynamodb.query-filter-legacy-ands-by-default-ors-under-conditional-operator` | `4df2823` | ported | `DynamoDb/QueryFilterLegacyAndsByDefaultOrsUnderConditionalOperator.cs` |
| `dynamodb.query-filter-legacy-comparison-operators-beyond-eq` | `4df2823` | ported | `DynamoDb/QueryFilterLegacyComparisonOperatorsBeyondEq.cs` |
| `dynamodb.query-filter-legacy-not-null-with-value-list-rejected` | `4df2823` | ported | `DynamoDb/QueryFilterLegacyNotNullWithValueListRejected.cs` |
| `dynamodb.query-gsi-four-range-key-schema-orders-narrows-and-reverses` | `4df2823` | ported | `DynamoDb/QueryGsiFourRangeKeySchemaOrdersNarrowsAndReverses.cs` |
| `dynamodb.query-gsi-four-range-key-schema-skipping-two-attributes-rejected` | `4df2823` | ported | `DynamoDb/QueryGsiFourRangeKeySchemaSkippingTwoAttributesRejected.cs` |
| `dynamodb.query-gsi-multi-range-key-schema-requires-equality-prefix` | `4df2823` | ported | `DynamoDb/QueryGsiMultiRangeKeySchemaRequiresEqualityPrefix.cs` |
| `dynamodb.query-indexes-capacity-sums-bytes-once-across-table-gsi-and-lsi` | `4df2823` | ported | `DynamoDb/QueryIndexesCapacitySumsBytesOnceAcrossTableGsiAndLsi.cs` |
| `dynamodb.query-key-condition` | `4df2823` | ported | `DynamoDb/QueryKeyCondition.cs` |
| `dynamodb.query-key-condition-document-path-forms-and-reserved-head-rejected` | `4df2823` | ported | `DynamoDb/QueryKeyConditionDocumentPathFormsAndReservedHeadRejected.cs` |
| `dynamodb.query-key-condition-duplicate-sort-key-clause-and-nested-path-rejected` | `4df2823` | ported | `DynamoDb/QueryKeyConditionDuplicateSortKeyClauseAndNestedPathRejected.cs` |
| `dynamodb.query-key-condition-expression-third-predicate-rejected` | `4df2823` | ported | `DynamoDb/QueryKeyConditionExpressionThirdPredicateRejected.cs` |
| `dynamodb.query-key-condition-ge-negative-n-sort-key-numeric-order` | `4df2823` | ported | `DynamoDb/QueryKeyConditionGeNegativeNSortKeyNumericOrder.cs` |
| `dynamodb.query-key-condition-non-key-missing-partition-begins-with-partition-rejected` | `4df2823` | ported | `DynamoDb/QueryKeyConditionNonKeyMissingPartitionBeginsWithPartitionRejected.cs` |
| `dynamodb.query-key-condition-operand-type-vs-schema-type-rejected` | `4df2823` | ported | `DynamoDb/QueryKeyConditionOperandTypeVsSchemaTypeRejected.cs` |
| `dynamodb.query-key-condition-syntax-error-and-attribute-type-function-rejected` | `4df2823` | ported | `DynamoDb/QueryKeyConditionSyntaxErrorAndAttributeTypeFunctionRejected.cs` |
| `dynamodb.query-key-condition-undefined-value-vs-bad-operand-precedence` | `4df2823` | ported | `DynamoDb/QueryKeyConditionUndefinedValueVsBadOperandPrecedence.cs` |
| `dynamodb.query-limit` | `4df2823` | ported | `DynamoDb/QueryLimit.cs` |
| `dynamodb.query-limit-applied-before-filter-expression` | `4df2823` | ported | `DynamoDb/QueryLimitAppliedBeforeFilterExpression.cs` |
| `dynamodb.query-limit-at-partition-count-keeps-lastevaluatedkey-scan-limit-above-count-drops-it` | `4df2823` | ported | `DynamoDb/QueryLimitAtPartitionCountKeepsLastevaluatedkeyScanLimitAboveCountDropsIt.cs` |
| `dynamodb.query-limit-zero-rejected` | `4df2823` | ported | `DynamoDb/QueryLimitZeroRejected.cs` |
| `dynamodb.query-matching-nothing-still-consumes-read-capacity` | `4df2823` | ported | `DynamoDb/QueryMatchingNothingStillConsumesReadCapacity.cs` |
| `dynamodb.query-mixing-key-conditions-and-expression-rejected` | `4df2823` | ported | `DynamoDb/QueryMixingKeyConditionsAndExpressionRejected.cs` |
| `dynamodb.query-natural-1mb-page-truncation` | `4df2823` | ported | `DynamoDb/QueryNatural1mbPageTruncation.cs` |
| `dynamodb.query-nested-non-redundant-parenthesized-key-condition` | `4df2823` | ported | `DynamoDb/QueryNestedNonRedundantParenthesizedKeyCondition.cs` |
| `dynamodb.query-never-returns-an-empty-page-with-a-last-evaluated-key` | `4df2823` | ported | `DynamoDb/QueryNeverReturnsAnEmptyPageWithALastEvaluatedKey.cs` |
| `dynamodb.query-on-index-consumed-capacity-attributes-the-index` | `4df2823` | ported | `DynamoDb/QueryOnIndexConsumedCapacityAttributesTheIndex.cs` |
| `dynamodb.query-pages-at-1mb-before-a-never-matching-filter` | `4df2823` | ported | `DynamoDb/QueryPagesAt1mbBeforeANeverMatchingFilter.cs` |
| `dynamodb.query-pagination-covers-every-item-exactly-once-in-key-order` | `4df2823` | ported | `DynamoDb/QueryPaginationCoversEveryItemExactlyOnceInKeyOrder.cs` |
| `dynamodb.query-pagination-exclusive-start-key` | `4df2823` | ported | `DynamoDb/QueryPaginationExclusiveStartKey.cs` |
| `dynamodb.query-pagination-no-duplicates` | `4df2823` | ported | `DynamoDb/QueryPaginationNoDuplicates.cs` |
| `dynamodb.query-reverse-pages-at-1mb-in-descending-key-order` | `4df2823` | ported | `DynamoDb/QueryReversePagesAt1mbInDescendingKeyOrder.cs` |
| `dynamodb.query-scan-exclusive-start-key-empty-value-rejected` | `4df2823` | ported | `DynamoDb/QueryScanExclusiveStartKeyEmptyValueRejected.cs` |
| `dynamodb.query-scan-index-forward-false` | `4df2823` | ported | `DynamoDb/QueryScanIndexForwardFalse.cs` |
| `dynamodb.query-unbalanced-nested-key-condition-rejected` | `4df2823` | ported | `DynamoDb/QueryUnbalancedNestedKeyConditionRejected.cs` |
| `dynamodb.query-undefined-expression-value-precedes-resource-not-found` | `4df2823` | ported | `DynamoDb/QueryUndefinedExpressionValuePrecedesResourceNotFound.cs` |
| `dynamodb.query-value-first-key-condition-operand-message-states-operator-as-written` | `4df2823` | ported | `DynamoDb/QueryValueFirstKeyConditionOperandMessageStatesOperatorAsWritten.cs` |
| `dynamodb.query-value-first-sort-key-comparison-and-size-of-absent-attribute` | `4df2823` | ported | `DynamoDb/QueryValueFirstSortKeyComparisonAndSizeOfAbsentAttribute.cs` |
| `dynamodb.re-enabling-a-stream-mints-a-new-descriptor` | `4df2823` | ported | `DynamoDb/ReEnablingAStreamMintsANewDescriptor.cs` |
| `dynamodb.replica-auto-scaling-describe-update` | `4df2823` | not ported | Application Auto Scaling |
| `dynamodb.replica-autoscaling-not-found` | `4df2823` | ported | `DynamoDb/ReplicaAutoscalingNotFound.cs` |
| `dynamodb.replica-autoscaling-policy-applied` | `4df2823` | not ported | Application Auto Scaling |
| `dynamodb.request-validation-precedes-missing-table-lookup-rejected` | `4df2823` | ported | `DynamoDb/RequestValidationPrecedesMissingTableLookupRejected.cs` |
| `dynamodb.reserved-word-bare-rejected-across-update-filter-and-projection` | `4df2823` | ported | `DynamoDb/ReservedWordBareRejectedAcrossUpdateFilterAndProjection.cs` |
| `dynamodb.reserved-word-needs-expression-attribute-name` | `4df2823` | ported | `DynamoDb/ReservedWordNeedsExpressionAttributeName.cs` |
| `dynamodb.resource-policy-delete-nonexistent-is-noop` | `4df2823` | ported | `DynamoDb/ResourcePolicyDeleteNonexistentIsNoop.cs` |
| `dynamodb.resource-policy-explicit-deny-blocks-access-rejected` | `4df2823` | not ported | IAM roles, role assumption and Lambda |
| `dynamodb.resource-policy-explicit-deny-on-getitem-rejected-put-still-allowed` | `4df2823` | ported | `DynamoDb/ResourcePolicyExplicitDenyOnGetitemRejectedPutStillAllowed.cs` |
| `dynamodb.resource-policy-lifecycle` | `4df2823` | ported | `DynamoDb/ResourcePolicyLifecycle.cs` |
| `dynamodb.resource-policy-not-found` | `4df2823` | ported | `DynamoDb/ResourcePolicyNotFound.cs` |
| `dynamodb.resource-policy-put-get-delete-lifecycle` | `4df2823` | ported | `DynamoDb/ResourcePolicyPutGetDeleteLifecycle.cs` |
| `dynamodb.resource-policy-revision-mismatch` | `4df2823` | ported | `DynamoDb/ResourcePolicyRevisionMismatch.cs` |
| `dynamodb.response-requestid-format-and-content-length-match-body-on-error` | `4df2823` | ported | `DynamoDb/ResponseRequestidFormatAndContentLengthMatchBodyOnError.cs` |
| `dynamodb.restore-backup-name-conflict` | `4df2823` | ported | `DynamoDb/RestoreBackupNameConflict.cs` |
| `dynamodb.restore-from-backup-drops-tags-stream-ttl-deletion-protection` | `4df2823` | ported | `DynamoDb/RestoreFromBackupDropsTagsStreamTtlDeletionProtection.cs` |
| `dynamodb.restore-from-backup-lifecycle` | `4df2823` | ported | `DynamoDb/RestoreFromBackupLifecycle.cs` |
| `dynamodb.restore-from-backup-not-found-rejected` | `4df2823` | ported | `DynamoDb/RestoreFromBackupNotFoundRejected.cs` |
| `dynamodb.restore-invalid-time` | `4df2823` | ported | `DynamoDb/RestoreInvalidTime.cs` |
| `dynamodb.restore-table-from-backup-creates-active-table` | `4df2823` | ported | `DynamoDb/RestoreTableFromBackupCreatesActiveTable.cs` |
| `dynamodb.restore-table-from-backup-sse-override-uses-customer-key` | `4df2823` | not ported | KMS keys |
| `dynamodb.restore-table-target-name-already-exists-rejected` | `4df2823` | ported | `DynamoDb/RestoreTableTargetNameAlreadyExistsRejected.cs` |
| `dynamodb.restore-table-target-name-conflict-rejected` | `4df2823` | ported | `DynamoDb/RestoreTableTargetNameConflictRejected.cs` |
| `dynamodb.restore-table-to-point-in-time-creates-active-table` | `4df2823` | ported | `DynamoDb/RestoreTableToPointInTimeCreatesActiveTable.cs` |
| `dynamodb.restore-to-earliest-restorable-time-excludes-later-writes` | `4df2823` | ported | `DynamoDb/RestoreToEarliestRestorableTimeExcludesLaterWrites.cs` |
| `dynamodb.restore-to-point-in-time-invalid-restore-time-rejected` | `4df2823` | ported | `DynamoDb/RestoreToPointInTimeInvalidRestoreTimeRejected.cs` |
| `dynamodb.restore-to-point-in-time-invalid-time-rejected` | `4df2823` | ported | `DynamoDb/RestoreToPointInTimeInvalidTimeRejected.cs` |
| `dynamodb.restore-to-point-in-time-lifecycle` | `4df2823` | ported | `DynamoDb/RestoreToPointInTimeLifecycle.cs` |
| `dynamodb.scan-all-projected-attributes-rejects-attributes-to-get` | `4df2823` | ported | `DynamoDb/ScanAllProjectedAttributesRejectsAttributesToGet.cs` |
| `dynamodb.scan-and-query-filter-ordering-comparison-on-a-set-attribute-rejected` | `4df2823` | ported | `DynamoDb/ScanAndQueryFilterOrderingComparisonOnASetAttributeRejected.cs` |
| `dynamodb.scan-and-query-filter-set-equality-compares-members-not-order-or-subset` | `4df2823` | ported | `DynamoDb/ScanAndQueryFilterSetEqualityComparesMembersNotOrderOrSubset.cs` |
| `dynamodb.scan-combined-filter-and-projection-does-not-reduce-consumed-capacity` | `4df2823` | ported | `DynamoDb/ScanCombinedFilterAndProjectionDoesNotReduceConsumedCapacity.cs` |
| `dynamodb.scan-conditional-operator-or-unions-legacy-filters` | `4df2823` | ported | `DynamoDb/ScanConditionalOperatorOrUnionsLegacyFilters.cs` |
| `dynamodb.scan-exclusive-start-key-naming-a-deleted-item-resumes-without-error` | `4df2823` | ported | `DynamoDb/ScanExclusiveStartKeyNamingADeletedItemResumesWithoutError.cs` |
| `dynamodb.scan-exclusive-start-key-naming-an-attribute-outside-the-key-schema-rejected` | `4df2823` | ported | `DynamoDb/ScanExclusiveStartKeyNamingAnAttributeOutsideTheKeySchemaRejected.cs` |
| `dynamodb.scan-filter-begins-with-non-string-operand-rejected` | `4df2823` | ported | `DynamoDb/ScanFilterBeginsWithNonStringOperandRejected.cs` |
| `dynamodb.scan-filter-between-binary-attribute-reversed-bounds-rejected` | `4df2823` | ported | `DynamoDb/ScanFilterBetweenBinaryAttributeReversedBoundsRejected.cs` |
| `dynamodb.scan-filter-between-binary-attribute-unsigned-byte-order-inclusive` | `4df2823` | ported | `DynamoDb/ScanFilterBetweenBinaryAttributeUnsignedByteOrderInclusive.cs` |
| `dynamodb.scan-filter-binary-attribute-unsigned-byte-order` | `4df2823` | ported | `DynamoDb/ScanFilterBinaryAttributeUnsignedByteOrder.cs` |
| `dynamodb.scan-filter-conditional-operator-document-value-equality` | `4df2823` | ported | `DynamoDb/ScanFilterConditionalOperatorDocumentValueEquality.cs` |
| `dynamodb.scan-filter-eq-and-contains-type-strict-across-s-n-b` | `4df2823` | ported | `DynamoDb/ScanFilterEqAndContainsTypeStrictAcrossSNB.cs` |
| `dynamodb.scan-filter-expression-le-ge-and-binary-begins-with` | `4df2823` | ported | `DynamoDb/ScanFilterExpressionLeGeAndBinaryBeginsWith.cs` |
| `dynamodb.scan-filter-expression-not-and-per-comparison-parens` | `4df2823` | ported | `DynamoDb/ScanFilterExpressionNotAndPerComparisonParens.cs` |
| `dynamodb.scan-filter-expression-ordering-comparison-non-orderable-operand-rejected` | `4df2823` | ported | `DynamoDb/ScanFilterExpressionOrderingComparisonNonOrderableOperandRejected.cs` |
| `dynamodb.scan-filter-expression-redundant-nested-parens-rejected` | `4df2823` | ported | `DynamoDb/ScanFilterExpressionRedundantNestedParensRejected.cs` |
| `dynamodb.scan-filter-legacy-contains-wrong-operand-count-rejected` | `4df2823` | ported | `DynamoDb/ScanFilterLegacyContainsWrongOperandCountRejected.cs` |
| `dynamodb.scan-filter-legacy-in-string-and-numeric` | `4df2823` | ported | `DynamoDb/ScanFilterLegacyInStringAndNumeric.cs` |
| `dynamodb.scan-filter-legacy-in-with-a-large-operand-list` | `4df2823` | ported | `DynamoDb/ScanFilterLegacyInWithALargeOperandList.cs` |
| `dynamodb.scan-filter-legacy-numeric-decimal-negative` | `4df2823` | ported | `DynamoDb/ScanFilterLegacyNumericDecimalNegative.cs` |
| `dynamodb.scan-filter-legacy-string-byte-order` | `4df2823` | ported | `DynamoDb/ScanFilterLegacyStringByteOrder.cs` |
| `dynamodb.scan-filter-ne-and-not-contains-on-missing-attribute` | `4df2823` | ported | `DynamoDb/ScanFilterNeAndNotContainsOnMissingAttribute.cs` |
| `dynamodb.scan-filter-size-covers-s-b-ss-l-m-not-n` | `4df2823` | ported | `DynamoDb/ScanFilterSizeCoversSBSsLMNotN.cs` |
| `dynamodb.scan-pages-at-1mb-with-and-without-never-matching-filter` | `4df2823` | ported | `DynamoDb/ScanPagesAt1mbWithAndWithoutNeverMatchingFilter.cs` |
| `dynamodb.scan-pagination-exclusive-start-key` | `4df2823` | ported | `DynamoDb/ScanPaginationExclusiveStartKey.cs` |
| `dynamodb.scan-parallel-segments` | `4df2823` | ported | `DynamoDb/ScanParallelSegments.cs` |
| `dynamodb.scan-query-conditional-operator-needs-two-filter-conditions-rejected` | `4df2823` | ported | `DynamoDb/ScanQueryConditionalOperatorNeedsTwoFilterConditionsRejected.cs` |
| `dynamodb.scan-query-legacy-and-expression-mixing-names-every-parameter-rejected` | `4df2823` | ported | `DynamoDb/ScanQueryLegacyAndExpressionMixingNamesEveryParameterRejected.cs` |
| `dynamodb.scan-returns-all-items` | `4df2823` | ported | `DynamoDb/ScanReturnsAllItems.cs` |
| `dynamodb.scan-select-all-attributes-on-partial-projection-index-rejected` | `4df2823` | ported | `DynamoDb/ScanSelectAllAttributesOnPartialProjectionIndexRejected.cs` |
| `dynamodb.scan-with-filter` | `4df2823` | ported | `DynamoDb/ScanWithFilter.cs` |
| `dynamodb.sdk-retry-replays-the-wire-and-only-the-token-stops-the-duplicate` | `4df2823` | ported | `DynamoDb/SdkRetryReplaysTheWireAndOnlyTheTokenStopsTheDuplicate.cs` |
| `dynamodb.search-vectors-consumed-capacity-is-vector-bytes-only-floored-and-scales` | `4df2823` | ported | `DynamoDb/SearchVectorsConsumedCapacityIsVectorBytesOnlyFlooredAndScales.cs` |
| `dynamodb.search-vectors-dimension-mismatch-rejected` | `4df2823` | ported | `DynamoDb/SearchVectorsDimensionMismatchRejected.cs` |
| `dynamodb.search-vectors-dot-product-order-and-score-precision` | `4df2823` | ported | `DynamoDb/SearchVectorsDotProductOrderAndScorePrecision.cs` |
| `dynamodb.search-vectors-expression-attribute-value-mismatch-rejected` | `4df2823` | ported | `DynamoDb/SearchVectorsExpressionAttributeValueMismatchRejected.cs` |
| `dynamodb.search-vectors-non-numeric-search-vector-element-rejected` | `4df2823` | ported | `DynamoDb/SearchVectorsNonNumericSearchVectorElementRejected.cs` |
| `dynamodb.search-vectors-not-found` | `4df2823` | ported | `DynamoDb/SearchVectorsNotFound.cs` |
| `dynamodb.search-vectors-orders-by-cosine-distance` | `4df2823` | ported | `DynamoDb/SearchVectorsOrdersByCosineDistance.cs` |
| `dynamodb.search-vectors-query-workflow` | `4df2823` | ported | `DynamoDb/SearchVectorsQueryWorkflow.cs` |
| `dynamodb.search-vectors-resource-not-found-rejected` | `4df2823` | ported | `DynamoDb/SearchVectorsResourceNotFoundRejected.cs` |
| `dynamodb.search-vectors-unknown-index-rejected` | `4df2823` | ported | `DynamoDb/SearchVectorsUnknownIndexRejected.cs` |
| `dynamodb.select-all-projected-attributes-requires-an-index-rejected` | `4df2823` | ported | `DynamoDb/SelectAllProjectedAttributesRequiresAnIndexRejected.cs` |
| `dynamodb.select-non-specific-values-reject-projection-expression` | `4df2823` | ported | `DynamoDb/SelectNonSpecificValuesRejectProjectionExpression.cs` |
| `dynamodb.server-side-point-in-time-recovery-enforced-after-enable` | `4df2823` | ported | `DynamoDb/ServerSidePointInTimeRecoveryEnforcedAfterEnable.cs` |
| `dynamodb.set-arithmetic-grouping-size-utf16-and-add-empty-string-to-set` | `4df2823` | ported | `DynamoDb/SetArithmeticGroupingSizeUtf16AndAddEmptyStringToSet.cs` |
| `dynamodb.set-arithmetic-on-missing-attribute-rejected` | `4df2823` | ported | `DynamoDb/SetArithmeticOnMissingAttributeRejected.cs` |
| `dynamodb.sets-reject-empty-and-duplicate-members` | `4df2823` | ported | `DynamoDb/SetsRejectEmptyAndDuplicateMembers.cs` |
| `dynamodb.sigv4-auth-missing-forms-give-missing-authentication-token` | `4df2823` | known gap: DynamoDbAuthentication | `DynamoDb/Sigv4AuthMissingFormsGiveMissingAuthenticationToken.cs` |
| `dynamodb.sigv4-query-string-presigned-list-tables-accepted` | `4df2823` | ported | `DynamoDb/Sigv4QueryStringPresignedListTablesAccepted.cs` |
| `dynamodb.sigv4-query-string-presigned-list-tables-tampered-signature-rejected` | `4df2823` | known gap: DynamoDbAuthentication | `DynamoDb/Sigv4QueryStringPresignedListTablesTamperedSignatureRejected.cs` |
| `dynamodb.sse-with-a-kms-alias-reports-the-resolved-key-arn` | `4df2823` | not ported | KMS keys |
| `dynamodb.stale-condition-update-rejected-leaves-counter-and-audit-trail-intact` | `4df2823` | ported | `DynamoDb/StaleConditionUpdateRejectedLeavesCounterAndAuditTrailIntact.cs` |
| `dynamodb.stream-arn-tag-list-untag-independent-of-table` | `4df2823` | ported | `DynamoDb/StreamArnTagListUntagIndependentOfTable.cs` |
| `dynamodb.stream-iterator-at-and-after-sequence-number-resume-exactly` | `4df2823` | ported | `DynamoDb/StreamIteratorAtAndAfterSequenceNumberResumeExactly.cs` |
| `dynamodb.stream-records-iterator-and-images` | `4df2823` | ported | `DynamoDb/StreamRecordsIteratorAndImages.cs` |
| `dynamodb.stream-small-limit-walk-sees-every-record-once-and-unknown-shard-not-found` | `4df2823` | ported | `DynamoDb/StreamSmallLimitWalkSeesEveryRecordOnceAndUnknownShardNotFound.cs` |
| `dynamodb.stream-view-type-cannot-be-changed-in-place` | `4df2823` | ported | `DynamoDb/StreamViewTypeCannotBeChangedInPlace.cs` |
| `dynamodb.string-revision-token-conditional-writes-refuse-stale-and-unissued-tokens` | `4df2823` | ported | `DynamoDb/StringRevisionTokenConditionalWritesRefuseStaleAndUnissuedTokens.cs` |
| `dynamodb.string-sort-key-ascii-byte-order-in-ranges-prefixes-and-paging` | `4df2823` | ported | `DynamoDb/StringSortKeyAsciiByteOrderInRangesPrefixesAndPaging.cs` |
| `dynamodb.string-sort-key-orders-by-utf8-bytes` | `4df2823` | ported | `DynamoDb/StringSortKeyOrdersByUtf8Bytes.cs` |
| `dynamodb.table-arn-accepted-wherever-table-name-is` | `4df2823` | ported | `DynamoDb/TableArnAcceptedWhereverTableNameIs.cs` |
| `dynamodb.table-arn-addresses-a-table-wherever-a-name-does` | `4df2823` | ported | `DynamoDb/TableArnAddressesATableWhereverANameDoes.cs` |
| `dynamodb.table-class-round-trips-and-applies-to-indexes` | `4df2823` | ported | `DynamoDb/TableClassRoundTripsAndAppliesToIndexes.cs` |
| `dynamodb.table-class-update-limit-exceeded-rejected` | `4df2823` | ported | `DynamoDb/TableClassUpdateLimitExceededRejected.cs` |
| `dynamodb.table-name-charset-and-length-validated-before-existence-rejected` | `4df2823` | ported | `DynamoDb/TableNameCharsetAndLengthValidatedBeforeExistenceRejected.cs` |
| `dynamodb.table-name-is-scoped-to-its-region` | `4df2823` | ported | `DynamoDb/TableNameIsScopedToItsRegion.cs` |
| `dynamodb.table-name-validation-precedes-existence-on-data-plane` | `4df2823` | ported | `DynamoDb/TableNameValidationPrecedesExistenceOnDataPlane.cs` |
| `dynamodb.table-replica-auto-scaling-update-reflected` | `4df2823` | not ported | Application Auto Scaling |
| `dynamodb.table-with-sort-key` | `4df2823` | ported | `DynamoDb/TableWithSortKey.cs` |
| `dynamodb.tag-overwrite-absent-untag-and-recreated-table-starts-untagged` | `4df2823` | ported | `DynamoDb/TagOverwriteAbsentUntagAndRecreatedTableStartsUntagged.cs` |
| `dynamodb.tag-resource-exceeds-tag-limit-rejected` | `4df2823` | ported | `DynamoDb/TagResourceExceedsTagLimitRejected.cs` |
| `dynamodb.tag-resource-nonexistent-stream-arn-rejected` | `4df2823` | ported | `DynamoDb/TagResourceNonexistentStreamArnRejected.cs` |
| `dynamodb.transact-condition-check-on-absent-item-passes-without-creating-it` | `4df2823` | ported | `DynamoDb/TransactConditionCheckOnAbsentItemPassesWithoutCreatingIt.cs` |
| `dynamodb.transact-get-and-write-items-table-arn-naming-another-region-rejected` | `4df2823` | ported | `DynamoDb/TransactGetAndWriteItemsTableArnNamingAnotherRegionRejected.cs` |
| `dynamodb.transact-get-items` | `4df2823` | ported | `DynamoDb/TransactGetItems.cs` |
| `dynamodb.transact-get-items-aggregate-size-over-4mb-rejected` | `4df2823` | ported | `DynamoDb/TransactGetItemsAggregateSizeOver4mbRejected.cs` |
| `dynamodb.transact-get-items-at-the-100-item-cap-across-two-tables` | `4df2823` | ported | `DynamoDb/TransactGetItemsAtThe100ItemCapAcrossTwoTables.cs` |
| `dynamodb.transact-get-items-bills-missing-items-and-reports-indexes-capacity` | `4df2823` | ported | `DynamoDb/TransactGetItemsBillsMissingItemsAndReportsIndexesCapacity.cs` |
| `dynamodb.transact-get-items-consumes-twice-a-strongly-consistent-read` | `4df2823` | ported | `DynamoDb/TransactGetItemsConsumesTwiceAStronglyConsistentRead.cs` |
| `dynamodb.transact-get-items-multi-table` | `4df2823` | ported | `DynamoDb/TransactGetItemsMultiTable.cs` |
| `dynamodb.transact-get-items-never-observes-a-torn-transfer` | `4df2823` | ported | `DynamoDb/TransactGetItemsNeverObservesATornTransfer.cs` |
| `dynamodb.transact-get-items-over-the-item-cap-rejected` | `4df2823` | ported | `DynamoDb/TransactGetItemsOverTheItemCapRejected.cs` |
| `dynamodb.transact-get-items-preserves-request-order` | `4df2823` | ported | `DynamoDb/TransactGetItemsPreservesRequestOrder.cs` |
| `dynamodb.transact-get-items-reports-capacity-per-table-as-a-list` | `4df2823` | ported | `DynamoDb/TransactGetItemsReportsCapacityPerTableAsAList.cs` |
| `dynamodb.transact-get-items-top-level-vs-cancellation-error-class` | `4df2823` | ported | `DynamoDb/TransactGetItemsTopLevelVsCancellationErrorClass.cs` |
| `dynamodb.transact-items-accept-placeholders-without-an-expression` | `4df2823` | ported | `DynamoDb/TransactItemsAcceptPlaceholdersWithoutAnExpression.cs` |
| `dynamodb.transact-oversized-hash-key-cancels-with-validation-error` | `4df2823` | ported | `DynamoDb/TransactOversizedHashKeyCancelsWithValidationError.cs` |
| `dynamodb.transact-token-replay-consumed-capacity-is-a-read-not-a-write` | `4df2823` | ported | `DynamoDb/TransactTokenReplayConsumedCapacityIsAReadNotAWrite.cs` |
| `dynamodb.transact-unused-expression-attribute-name-on-condition-check-and-get-rejected` | `4df2823` | ported | `DynamoDb/TransactUnusedExpressionAttributeNameOnConditionCheckAndGetRejected.cs` |
| `dynamodb.transact-update-condition-needs-item-cancels-the-passing-sibling` | `4df2823` | ported | `DynamoDb/TransactUpdateConditionNeedsItemCancelsThePassingSibling.cs` |
| `dynamodb.transact-write-aggregate-size-over-4mb-rejected` | `4df2823` | ported | `DynamoDb/TransactWriteAggregateSizeOver4mbRejected.cs` |
| `dynamodb.transact-write-all-or-nothing` | `4df2823` | ported | `DynamoDb/TransactWriteAllOrNothing.cs` |
| `dynamodb.transact-write-condition-check-and-conditional-put-billing` | `4df2823` | ported | `DynamoDb/TransactWriteConditionCheckAndConditionalPutBilling.cs` |
| `dynamodb.transact-write-condition-check-failure-returns-item-rejected` | `4df2823` | ported | `DynamoDb/TransactWriteConditionCheckFailureReturnsItemRejected.cs` |
| `dynamodb.transact-write-condition-non-orderable-literal-rejected` | `4df2823` | ported | `DynamoDb/TransactWriteConditionNonOrderableLiteralRejected.cs` |
| `dynamodb.transact-write-conflict-reason-is-transaction-conflict` | `4df2823` | ported | `DynamoDb/TransactWriteConflictReasonIsTransactionConflict.cs` |
| `dynamodb.transact-write-delete-condition-failure-cancels-transaction` | `4df2823` | ported | `DynamoDb/TransactWriteDeleteConditionFailureCancelsTransaction.cs` |
| `dynamodb.transact-write-exceeds-100-items` | `4df2823` | ported | `DynamoDb/TransactWriteExceeds100Items.cs` |
| `dynamodb.transact-write-expression-parse-errors-rejected-top-level` | `4df2823` | ported | `DynamoDb/TransactWriteExpressionParseErrorsRejectedTopLevel.cs` |
| `dynamodb.transact-write-idempotency-mismatch` | `4df2823` | ported | `DynamoDb/TransactWriteIdempotencyMismatch.cs` |
| `dynamodb.transact-write-idempotency-token` | `4df2823` | ported | `DynamoDb/TransactWriteIdempotencyToken.cs` |
| `dynamodb.transact-write-items-cancelled-by-condition-failure-reports-no-consumed-capacity` | `4df2823` | ported | `DynamoDb/TransactWriteItemsCancelledByConditionFailureReportsNoConsumedCapacity.cs` |
| `dynamodb.transact-write-items-client-token-idempotent-mismatch-rejected` | `4df2823` | ported | `DynamoDb/TransactWriteItemsClientTokenIdempotentMismatchRejected.cs` |
| `dynamodb.transact-write-items-condition-expression-defects-match-putitem-message` | `4df2823` | ported | `DynamoDb/TransactWriteItemsConditionExpressionDefectsMatchPutitemMessage.cs` |
| `dynamodb.transact-write-items-consumes-twice-a-plain-write` | `4df2823` | ported | `DynamoDb/TransactWriteItemsConsumesTwiceAPlainWrite.cs` |
| `dynamodb.transact-write-items-delete-with-failed-condition-cancels-other-deletes-too` | `4df2823` | ported | `DynamoDb/TransactWriteItemsDeleteWithFailedConditionCancelsOtherDeletesToo.cs` |
| `dynamodb.transact-write-items-duplicate-key-rejected` | `4df2823` | ported | `DynamoDb/TransactWriteItemsDuplicateKeyRejected.cs` |
| `dynamodb.transact-write-items-empty-index-key-validates-not-cancels` | `4df2823` | ported | `DynamoDb/TransactWriteItemsEmptyIndexKeyValidatesNotCancels.cs` |
| `dynamodb.transact-write-items-expression-size-limit-applies-per-action` | `4df2823` | ported | `DynamoDb/TransactWriteItemsExpressionSizeLimitAppliesPerAction.cs` |
| `dynamodb.transact-write-items-first-fatal-defect-reported-depends-on-defect-class` | `4df2823` | ported | `DynamoDb/TransactWriteItemsFirstFatalDefectReportedDependsOnDefectClass.cs` |
| `dynamodb.transact-write-items-index-key-type-mismatch-cancels-not-validates` | `4df2823` | ported | `DynamoDb/TransactWriteItemsIndexKeyTypeMismatchCancelsNotValidates.cs` |
| `dynamodb.transact-write-items-invalid-table-name-names-member-path` | `4df2823` | ported | `DynamoDb/TransactWriteItemsInvalidTableNameNamesMemberPath.cs` |
| `dynamodb.transact-write-items-missing-table-is-resource-not-found` | `4df2823` | ported | `DynamoDb/TransactWriteItemsMissingTableIsResourceNotFound.cs` |
| `dynamodb.transact-write-items-near-4mb-aggregate-commits` | `4df2823` | ported | `DynamoDb/TransactWriteItemsNear4mbAggregateCommits.cs` |
| `dynamodb.transact-write-items-nesting-violation-is-top-level-validation-not-cancellation` | `4df2823` | ported | `DynamoDb/TransactWriteItemsNestingViolationIsTopLevelValidationNotCancellation.cs` |
| `dynamodb.transact-write-items-over-the-item-cap-rejected` | `4df2823` | ported | `DynamoDb/TransactWriteItemsOverTheItemCapRejected.cs` |
| `dynamodb.transact-write-items-overlength-token-masks-other-violations-rejected` | `4df2823` | ported | `DynamoDb/TransactWriteItemsOverlengthTokenMasksOtherViolationsRejected.cs` |
| `dynamodb.transact-write-items-parenthesized-conditions-apply-and-cancel` | `4df2823` | ported | `DynamoDb/TransactWriteItemsParenthesizedConditionsApplyAndCancel.cs` |
| `dynamodb.transact-write-items-put-and-update-accept-flat-409600-byte-item` | `4df2823` | ported | `DynamoDb/TransactWriteItemsPutAndUpdateAcceptFlat409600ByteItem.cs` |
| `dynamodb.transact-write-items-put-condition-value-nesting-depth-rejected` | `4df2823` | ported | `DynamoDb/TransactWriteItemsPutConditionValueNestingDepthRejected.cs` |
| `dynamodb.transact-write-items-put-over-item-size-limit` | `4df2823` | ported | `DynamoDb/TransactWriteItemsPutOverItemSizeLimit.cs` |
| `dynamodb.transact-write-items-put-string-set-with-zero-members-rejected` | `4df2823` | ported | `DynamoDb/TransactWriteItemsPutStringSetWithZeroMembersRejected.cs` |
| `dynamodb.transact-write-items-several-deletes-of-distinct-items-all-apply` | `4df2823` | ported | `DynamoDb/TransactWriteItemsSeveralDeletesOfDistinctItemsAllApply.cs` |
| `dynamodb.transact-write-items-token-replay-bills-exact-capacity-for-item-size` | `4df2823` | ported | `DynamoDb/TransactWriteItemsTokenReplayBillsExactCapacityForItemSize.cs` |
| `dynamodb.transact-write-items-unused-expression-attribute-value-on-second-action-rejected` | `4df2823` | ported | `DynamoDb/TransactWriteItemsUnusedExpressionAttributeValueOnSecondActionRejected.cs` |
| `dynamodb.transact-write-items-update-attribute-not-exists-upserts` | `4df2823` | ported | `DynamoDb/TransactWriteItemsUpdateAttributeNotExistsUpserts.cs` |
| `dynamodb.transact-write-items-update-grows-item-over-size-limit` | `4df2823` | ported | `DynamoDb/TransactWriteItemsUpdateGrowsItemOverSizeLimit.cs` |
| `dynamodb.transact-write-items-update-key-and-empty-index-key-rejections` | `4df2823` | ported | `DynamoDb/TransactWriteItemsUpdateKeyAndEmptyIndexKeyRejections.cs` |
| `dynamodb.transact-write-items-update-over-size-limit-new-and-existing-item-both-regions` | `4df2823` | ported | `DynamoDb/TransactWriteItemsUpdateOverSizeLimitNewAndExistingItemBothRegions.cs` |
| `dynamodb.transact-write-multi-table-atomicity` | `4df2823` | ported | `DynamoDb/TransactWriteMultiTableAtomicity.cs` |
| `dynamodb.transact-write-skew-on-absent-keys-never-commits-both` | `4df2823` | ported | `DynamoDb/TransactWriteSkewOnAbsentKeysNeverCommitsBoth.cs` |
| `dynamodb.transact-write-stale-version-update-cancels-its-companion-put` | `4df2823` | ported | `DynamoDb/TransactWriteStaleVersionUpdateCancelsItsCompanionPut.cs` |
| `dynamodb.transact-write-token-dedupes-under-concurrent-submission` | `4df2823` | ported | `DynamoDb/TransactWriteTokenDedupesUnderConcurrentSubmission.cs` |
| `dynamodb.transact-write-token-replay-does-not-reapply` | `4df2823` | ported | `DynamoDb/TransactWriteTokenReplayDoesNotReapply.cs` |
| `dynamodb.transact-wrong-typed-key-cancels-and-empty-put-key-validates` | `4df2823` | ported | `DynamoDb/TransactWrongTypedKeyCancelsAndEmptyPutKeyValidates.cs` |
| `dynamodb.transactional-read-costs-double-a-strongly-consistent-read` | `4df2823` | ported | `DynamoDb/TransactionalReadCostsDoubleAStronglyConsistentRead.cs` |
| `dynamodb.ttl-configuration` | `4df2823` | ported | `DynamoDb/TtlConfiguration.cs` |
| `dynamodb.ttl-disable-within-modification-interval-rejected` | `4df2823` | ported | `DynamoDb/TtlDisableWithinModificationIntervalRejected.cs` |
| `dynamodb.ttl-enable-disable` | `4df2823` | ported | `DynamoDb/TtlEnableDisable.cs` |
| `dynamodb.ttl-expired-item-auto-deletion` | `4df2823` | ported | `DynamoDb/TtlExpiredItemAutoDeletion.cs` |
| `dynamodb.ttl-rename-while-enabled-rejected` | `4df2823` | ported | `DynamoDb/TtlRenameWhileEnabledRejected.cs` |
| `dynamodb.unbalanced-parenthesized-condition-expression-rejected` | `4df2823` | ported | `DynamoDb/UnbalancedParenthesizedConditionExpressionRejected.cs` |
| `dynamodb.unused-expression-attribute-value-and-name-rejected` | `4df2823` | ported | `DynamoDb/UnusedExpressionAttributeValueAndNameRejected.cs` |
| `dynamodb.update-continuous-backups-nonexistent` | `4df2823` | ported | `DynamoDb/UpdateContinuousBackupsNonexistent.cs` |
| `dynamodb.update-delete-last-set-element-removes-attribute` | `4df2823` | ported | `DynamoDb/UpdateDeleteLastSetElementRemovesAttribute.cs` |
| `dynamodb.update-expression-add-number` | `4df2823` | ported | `DynamoDb/UpdateExpressionAddNumber.cs` |
| `dynamodb.update-expression-add-to-set` | `4df2823` | ported | `DynamoDb/UpdateExpressionAddToSet.cs` |
| `dynamodb.update-expression-delete-from-set` | `4df2823` | ported | `DynamoDb/UpdateExpressionDeleteFromSet.cs` |
| `dynamodb.update-expression-malformed-syntax-rejected` | `4df2823` | ported | `DynamoDb/UpdateExpressionMalformedSyntaxRejected.cs` |
| `dynamodb.update-expression-remove` | `4df2823` | ported | `DynamoDb/UpdateExpressionRemove.cs` |
| `dynamodb.update-expression-set` | `4df2823` | ported | `DynamoDb/UpdateExpressionSet.cs` |
| `dynamodb.update-item-add-delete-empty-set-operand-rejected` | `4df2823` | ported | `DynamoDb/UpdateItemAddDeleteEmptySetOperandRejected.cs` |
| `dynamodb.update-item-add-delete-empty-string-set-member-and-zero-length-binary-set-member` | `4df2823` | ported | `DynamoDb/UpdateItemAddDeleteEmptyStringSetMemberAndZeroLengthBinarySetMember.cs` |
| `dynamodb.update-item-add-type-mismatch` | `4df2823` | ported | `DynamoDb/UpdateItemAddTypeMismatch.cs` |
| `dynamodb.update-item-and-transact-update-empty-gsi-and-lsi-keys-rejected` | `4df2823` | ported | `DynamoDb/UpdateItemAndTransactUpdateEmptyGsiAndLsiKeysRejected.cs` |
| `dynamodb.update-item-chained-arithmetic-operators-on-list-index-rejected` | `4df2823` | ported | `DynamoDb/UpdateItemChainedArithmeticOperatorsOnListIndexRejected.cs` |
| `dynamodb.update-item-condition-expression-over-4096-bytes-rejected` | `4df2823` | ported | `DynamoDb/UpdateItemConditionExpressionOver4096BytesRejected.cs` |
| `dynamodb.update-item-expression-substitution-total-size-over-2mb-rejected` | `4df2823` | ported | `DynamoDb/UpdateItemExpressionSubstitutionTotalSizeOver2mbRejected.cs` |
| `dynamodb.update-item-growth-over-size-limit-with-all-projected-lsi-entry` | `4df2823` | ported | `DynamoDb/UpdateItemGrowthOverSizeLimitWithAllProjectedLsiEntry.cs` |
| `dynamodb.update-item-if-not-exists-document-path-fallback-and-existing-subtract` | `4df2823` | ported | `DynamoDb/UpdateItemIfNotExistsDocumentPathFallbackAndExistingSubtract.cs` |
| `dynamodb.update-item-if-not-exists-second-operand-missing-attribute-rejected` | `4df2823` | ported | `DynamoDb/UpdateItemIfNotExistsSecondOperandMissingAttributeRejected.cs` |
| `dynamodb.update-item-if-not-exists-size-checked-before-condition` | `4df2823` | ported | `DynamoDb/UpdateItemIfNotExistsSizeCheckedBeforeCondition.cs` |
| `dynamodb.update-item-index-capacity-ladder-follows-key-vs-projected-change` | `4df2823` | known gap: EmulatorBudget | `DynamoDb/UpdateItemIndexCapacityLadderFollowsKeyVsProjectedChange.cs` |
| `dynamodb.update-item-key-attribute-immutable` | `4df2823` | ported | `DynamoDb/UpdateItemKeyAttributeImmutable.cs` |
| `dynamodb.update-item-key-schema-mismatch` | `4df2823` | ported | `DynamoDb/UpdateItemKeySchemaMismatch.cs` |
| `dynamodb.update-item-lsi-key-type-mismatch-rejected` | `4df2823` | ported | `DynamoDb/UpdateItemLsiKeyTypeMismatchRejected.cs` |
| `dynamodb.update-item-lsi-sort-key-move-and-combined-gsi-lsi-move` | `4df2823` | ported | `DynamoDb/UpdateItemLsiSortKeyMoveAndCombinedGsiLsiMove.cs` |
| `dynamodb.update-item-malformed-or-overflowing-number-rejected` | `4df2823` | ported | `DynamoDb/UpdateItemMalformedOrOverflowingNumberRejected.cs` |
| `dynamodb.update-item-mixing-attribute-updates-and-expression-rejected` | `4df2823` | ported | `DynamoDb/UpdateItemMixingAttributeUpdatesAndExpressionRejected.cs` |
| `dynamodb.update-item-nested-arithmetic-mixed-clauses-and-if-not-exists-parent` | `4df2823` | ported | `DynamoDb/UpdateItemNestedArithmeticMixedClausesAndIfNotExistsParent.cs` |
| `dynamodb.update-item-number-set-condition-exact-at-full-precision` | `4df2823` | ported | `DynamoDb/UpdateItemNumberSetConditionExactAtFullPrecision.cs` |
| `dynamodb.update-item-numeric-set-precision-preserved` | `4df2823` | ported | `DynamoDb/UpdateItemNumericSetPrecisionPreserved.cs` |
| `dynamodb.update-item-remove-missing-map-key-noop-and-set-empty-list-index` | `4df2823` | ported | `DynamoDb/UpdateItemRemoveMissingMapKeyNoopAndSetEmptyListIndex.cs` |
| `dynamodb.update-item-request-shape-byte-charges-by-clause-kind` | `4df2823` | ported | `DynamoDb/UpdateItemRequestShapeByteChargesByClauseKind.cs` |
| `dynamodb.update-item-return-values-distinguishes-all-five-arms` | `4df2823` | ported | `DynamoDb/UpdateItemReturnValuesDistinguishesAllFiveArms.cs` |
| `dynamodb.update-item-return-values-omit-attributes-when-no-value-qualifies` | `4df2823` | ported | `DynamoDb/UpdateItemReturnValuesOmitAttributesWhenNoValueQualifies.cs` |
| `dynamodb.update-item-set-empty-string-set-rejected-top-and-nested` | `4df2823` | ported | `DynamoDb/UpdateItemSetEmptyStringSetRejectedTopAndNested.cs` |
| `dynamodb.update-item-set-list-append-add-exceeding-400kb-rejected` | `4df2823` | ported | `DynamoDb/UpdateItemSetListAppendAddExceeding400kbRejected.cs` |
| `dynamodb.update-item-set-list-index-on-missing-item-rejected` | `4df2823` | ported | `DynamoDb/UpdateItemSetListIndexOnMissingItemRejected.cs` |
| `dynamodb.update-item-set-list-preserves-element-order` | `4df2823` | ported | `DynamoDb/UpdateItemSetListPreservesElementOrder.cs` |
| `dynamodb.update-item-set-remove-fixed-action-cost-ceiling` | `4df2823` | ported | `DynamoDb/UpdateItemSetRemoveFixedActionCostCeiling.cs` |
| `dynamodb.update-item-set-size-ceiling-bisected-and-stable` | `4df2823` | ported | `DynamoDb/UpdateItemSetSizeCeilingBisectedAndStable.cs` |
| `dynamodb.update-item-set-ss-sole-empty-string-member-top-and-nested` | `4df2823` | ported | `DynamoDb/UpdateItemSetSsSoleEmptyStringMemberTopAndNested.cs` |
| `dynamodb.update-item-set-to-current-value-still-enforces-condition-expression-rejected` | `4df2823` | ported | `DynamoDb/UpdateItemSetToCurrentValueStillEnforcesConditionExpressionRejected.cs` |
| `dynamodb.update-item-size-ceiling-counts-key-and-untouched-attribute-bytes` | `4df2823` | ported | `DynamoDb/UpdateItemSizeCeilingCountsKeyAndUntouchedAttributeBytes.cs` |
| `dynamodb.update-item-updated-new-includes-unchanged-set-value-and-upsert` | `4df2823` | ported | `DynamoDb/UpdateItemUpdatedNewIncludesUnchangedSetValueAndUpsert.cs` |
| `dynamodb.update-item-updated-new-old-rebuild-nested-leaves-only` | `4df2823` | ported | `DynamoDb/UpdateItemUpdatedNewOldRebuildNestedLeavesOnly.cs` |
| `dynamodb.update-item-without-actions-upserts-key-only-item` | `4df2823` | ported | `DynamoDb/UpdateItemWithoutActionsUpsertsKeyOnlyItem.cs` |
| `dynamodb.update-list-append-on-ghost-attribute-rejected` | `4df2823` | ported | `DynamoDb/UpdateListAppendOnGhostAttributeRejected.cs` |
| `dynamodb.update-remove-add-delete-through-invalid-path-rejected` | `4df2823` | ported | `DynamoDb/UpdateRemoveAddDeleteThroughInvalidPathRejected.cs` |
| `dynamodb.update-return-values` | `4df2823` | ported | `DynamoDb/UpdateReturnValues.cs` |
| `dynamodb.update-return-values-updated-old-and-new-scope` | `4df2823` | ported | `DynamoDb/UpdateReturnValuesUpdatedOldAndNewScope.cs` |
| `dynamodb.update-set-arithmetic-and-list-append-type-mismatch-rejected` | `4df2823` | ported | `DynamoDb/UpdateSetArithmeticAndListAppendTypeMismatchRejected.cs` |
| `dynamodb.update-set-replaces-the-existing-value-without-merging` | `4df2823` | ported | `DynamoDb/UpdateSetReplacesTheExistingValueWithoutMerging.cs` |
| `dynamodb.update-set-through-missing-intermediate-map-rejected` | `4df2823` | ported | `DynamoDb/UpdateSetThroughMissingIntermediateMapRejected.cs` |
| `dynamodb.update-set-through-missing-map-path-rejected` | `4df2823` | ported | `DynamoDb/UpdateSetThroughMissingMapPathRejected.cs` |
| `dynamodb.update-table-billing-mode-pay-per-request-no-op-accepted` | `4df2823` | ported | `DynamoDb/UpdateTableBillingModePayPerRequestNoOpAccepted.cs` |
| `dynamodb.update-table-billing-mode-provisioned-same-value-rejected` | `4df2823` | ported | `DynamoDb/UpdateTableBillingModeProvisionedSameValueRejected.cs` |
| `dynamodb.update-table-billing-mode-transitions-updating-to-active` | `4df2823` | ported | `DynamoDb/UpdateTableBillingModeTransitionsUpdatingToActive.cs` |
| `dynamodb.update-table-creates-at-most-one-index-per-call` | `4df2823` | ported | `DynamoDb/UpdateTableCreatesAtMostOneIndexPerCall.cs` |
| `dynamodb.update-table-decrease-after-increase-records-number-of-decreases-today` | `4df2823` | ported | `DynamoDb/UpdateTableDecreaseAfterIncreaseRecordsNumberOfDecreasesToday.cs` |
| `dynamodb.update-table-delete-of-an-absent-gsi-rejected` | `4df2823` | ported | `DynamoDb/UpdateTableDeleteOfAnAbsentGsiRejected.cs` |
| `dynamodb.update-table-duplicate-and-bulk-gsi-delete-rejected` | `4df2823` | ported | `DynamoDb/UpdateTableDuplicateAndBulkGsiDeleteRejected.cs` |
| `dynamodb.update-table-exceeds-limit-rejected` | `4df2823` | ported | `DynamoDb/UpdateTableExceedsLimitRejected.cs` |
| `dynamodb.update-table-gsi-attribute-definitions-track-live-keys` | `4df2823` | ported | `DynamoDb/UpdateTableGsiAttributeDefinitionsTrackLiveKeys.cs` |
| `dynamodb.update-table-gsi-delete-targeting-lsi-name-rejected` | `4df2823` | ported | `DynamoDb/UpdateTableGsiDeleteTargetingLsiNameRejected.cs` |
| `dynamodb.update-table-on-a-missing-table-is-resource-not-found` | `4df2823` | ported | `DynamoDb/UpdateTableOnAMissingTableIsResourceNotFound.cs` |
| `dynamodb.update-table-rejected-while-a-prior-throughput-change-is-still-applying` | `4df2823` | ported | `DynamoDb/UpdateTableRejectedWhileAPriorThroughputChangeIsStillApplying.cs` |
| `dynamodb.update-table-rejects-a-duplicate-index-name` | `4df2823` | ported | `DynamoDb/UpdateTableRejectsADuplicateIndexName.cs` |
| `dynamodb.update-table-sets-and-clears-the-on-demand-read-ceiling` | `4df2823` | ported | `DynamoDb/UpdateTableSetsAndClearsTheOnDemandReadCeiling.cs` |
| `dynamodb.update-table-sse-nonexistent-cmk-rejected` | `4df2823` | not ported | KMS keys |
| `dynamodb.update-table-sse-switches-owned-managed-customer-key` | `4df2823` | not ported | KMS keys |
| `dynamodb.update-table-throughput-on-on-demand-table-and-zero-capacity-rejected` | `4df2823` | ported | `DynamoDb/UpdateTableThroughputOnOnDemandTableAndZeroCapacityRejected.cs` |
| `dynamodb.update-table-vector-index-requires-pay-per-request` | `4df2823` | ported | `DynamoDb/UpdateTableVectorIndexRequiresPayPerRequest.cs` |
| `dynamodb.update-ttl-attribute-name-exceeding-max-length-rejected` | `4df2823` | ported | `DynamoDb/UpdateTtlAttributeNameExceedingMaxLengthRejected.cs` |
| `dynamodb.update-ttl-invalid-attribute` | `4df2823` | ported | `DynamoDb/UpdateTtlInvalidAttribute.cs` |
| `dynamodb.validation-messages-are-customer-intelligible` | `4df2823` | ported | `DynamoDb/ValidationMessagesAreCustomerIntelligible.cs` |
| `dynamodb.validation-messages-name-the-concept-without-implementation-leakage` | `4df2823` | ported | `DynamoDb/ValidationMessagesNameTheConceptWithoutImplementationLeakage.cs` |
| `dynamodb.vector-capacity-sized-by-dimension-count-not-number-width` | `4df2823` | ported | `DynamoDb/VectorCapacitySizedByDimensionCountNotNumberWidth.cs` |
| `dynamodb.vector-index-4096-dimensions-and-hash-inline-filter-search-schema-described-back` | `4df2823` | ported | `DynamoDb/VectorIndex4096DimensionsAndHashInlineFilterSearchSchemaDescribedBack.cs` |
| `dynamodb.vector-index-creating-blocks-other-index-but-accepts-self-delete` | `4df2823` | ported | `DynamoDb/VectorIndexCreatingBlocksOtherIndexButAcceptsSelfDelete.cs` |
| `dynamodb.vector-index-creating-blocks-table-and-index-delete` | `4df2823` | ported | `DynamoDb/VectorIndexCreatingBlocksTableAndIndexDelete.cs` |
| `dynamodb.vector-index-query-and-scan-rejected` | `4df2823` | ported | `DynamoDb/VectorIndexQueryAndScanRejected.cs` |
| `dynamodb.vector-item-size-gate-counts-stored-number-bytes` | `4df2823` | ported | `DynamoDb/VectorItemSizeGateCountsStoredNumberBytes.cs` |
| `dynamodb.vector-write-request-bytes-floor-and-per-dimension-cost` | `4df2823` | ported | `DynamoDb/VectorWriteRequestBytesFloorAndPerDimensionCost.cs` |
| `dynamodb.vector-write-request-bytes-name-and-projection-terms` | `4df2823` | ported | `DynamoDb/VectorWriteRequestBytesNameAndProjectionTerms.cs` |
| `dynamodb.write-capacity-counts-list-and-map-element-overhead` | `4df2823` | ported | `DynamoDb/WriteCapacityCountsListAndMapElementOverhead.cs` |
| `dynamodb.write-capacity-per-gsi-counts-index-inserts-removals-and-projected-size` | `4df2823` | ported | `DynamoDb/WriteCapacityPerGsiCountsIndexInsertsRemovalsAndProjectedSize.cs` |
| `dynamodb.write-condition-parenthesized-forms-evaluate-like-unparenthesized` | `4df2823` | ported | `DynamoDb/WriteConditionParenthesizedFormsEvaluateLikeUnparenthesized.cs` |
| `dynamodb.write-conditional-operator-needs-two-expected-conditions-rejected` | `4df2823` | ported | `DynamoDb/WriteConditionalOperatorNeedsTwoExpectedConditionsRejected.cs` |
| `dynamodb.x-amz-crc32-matches-body-on-success-and-error` | `4df2823` | ported | `DynamoDb/XAmzCrc32MatchesBodyOnSuccessAndError.cs` |
| `dynamodb.zero-at-exponent-boundary-rejected-one-step-past-range` | `4df2823` | ported | `DynamoDb/ZeroAtExponentBoundaryRejectedOneStepPastRange.cs` |
| `dynamodb.zero-with-out-of-range-exponent-rejected` | `4df2823` | ported | `DynamoDb/ZeroWithOutOfRangeExponentRejected.cs` |

## behavioral/dynamodbstreams

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `dynamodbstreams.closed-shard-get-records-ends-with-a-null-next-shard-iterator` | `4df2823` | ported | `DynamoDbStreams/ClosedShardGetRecordsEndsWithANullNextShardIterator.cs` |
| `dynamodbstreams.concurrent-writes-each-appear-exactly-once` | `4df2823` | ported | `DynamoDbStreams/ConcurrentWritesEachAppearExactlyOnce.cs` |
| `dynamodbstreams.describe-stream-shard-pagination-covers-all-shards` | `4df2823` | ported | `DynamoDbStreams/DescribeStreamShardPaginationCoversAllShards.cs` |
| `dynamodbstreams.disable-closes-shards-and-reenable-mints-new-stream` | `4df2823` | ported | `DynamoDbStreams/DisableClosesShardsAndReenableMintsNewStream.cs` |
| `dynamodbstreams.foreign-sequence-number-rejected` | `4df2823` | ported | `DynamoDbStreams/ForeignSequenceNumberRejected.cs` |
| `dynamodbstreams.get-records-limit-pages-one-shard-without-gaps-or-repeats` | `4df2823` | ported | `DynamoDbStreams/GetRecordsLimitPagesOneShardWithoutGapsOrRepeats.cs` |
| `dynamodbstreams.iterator-types-select-expected-records` | `4df2823` | ported | `DynamoDbStreams/IteratorTypesSelectExpectedRecords.cs` |
| `dynamodbstreams.keys-only-view-omits-both-images` | `4df2823` | ported | `DynamoDbStreams/KeysOnlyViewOmitsBothImages.cs` |
| `dynamodbstreams.list-streams-limit-over-100-rejected` | `4df2823` | ported | `DynamoDbStreams/ListStreamsLimitOver100Rejected.cs` |
| `dynamodbstreams.list-streams-unfiltered-includes-new-table-stream` | `4df2823` | ported | `DynamoDbStreams/ListStreamsUnfilteredIncludesNewTableStream.cs` |
| `dynamodbstreams.malformed-shard-iterator-rejected` | `4df2823` | ported | `DynamoDbStreams/MalformedShardIteratorRejected.cs` |
| `dynamodbstreams.new-image-view-omits-old-image` | `4df2823` | ported | `DynamoDbStreams/NewImageViewOmitsOldImage.cs` |
| `dynamodbstreams.nonexistent-stream-arn-rejected` | `4df2823` | ported | `DynamoDbStreams/NonexistentStreamArnRejected.cs` |
| `dynamodbstreams.old-image-view-omits-new-image` | `4df2823` | ported | `DynamoDbStreams/OldImageViewOmitsNewImage.cs` |
| `dynamodbstreams.shard-record-order-matches-write-order` | `4df2823` | ported | `DynamoDbStreams/ShardRecordOrderMatchesWriteOrder.cs` |
| `dynamodbstreams.stream-record-creation-time-tracks-the-live-clock` | `4df2823` | ported | `DynamoDbStreams/StreamRecordCreationTimeTracksTheLiveClock.cs` |

## behavioral/eventbridgev2

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `eventbridgev2.create-event-bus-name-collision-different-token-rejected` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.create-event-bus-same-client-token-is-idempotent` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.create-subscriber-client-token-replay-idempotent-and-mismatch-rejected` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.delete-event-bus-blocked-by-event-source-until-revoked` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.delete-event-bus-with-subscriber-rejected-until-removed` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.event-bus-create-describe-list-delete-lifecycle` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.event-bus-tags-do-not-propagate-to-subscribers-or-event-sources` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.event-source-lifecycle-against-active-bus` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.point-in-time-starting-point-outside-allowed-range-rejected` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.put-events-partial-failure-reports-per-entry-error` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.recreated-event-bus-same-name-gets-new-identifier` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.resource-policy-conflicting-revision-rejected` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.resource-policy-put-get-list-round-trip` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.revoke-resource-marks-subscriber-terminal-and-outlives-bus-deletion` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.subscriber-name-collision-on-same-bus-rejected-but-allowed-on-another` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.subscriber-put-events-delivers-to-sqs` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2.tag-resource-and-list-tags-round-trip` | `4df2823` | not ported | EventBridge v2 |

## behavioral/eventbridgev2__iam__sns__sqs

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `eventbridgev2__iam__sns__sqs.role-without-sns-publish-dead-letters-and-topic-receives-nothing` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sns__sqs.sns-topic-target-publishes-one-event-per-message` | `4df2823` | not ported | EventBridge v2 |

## behavioral/eventbridgev2__iam__sqs

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `eventbridgev2__iam__sqs.dead-letter-record-names-access-denied-when-role-cannot-reach-target` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.detail-pattern-matches-put-events-envelope-not-raw-payload` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.event-bus-target-forwards-as-new-event-with-evaluated-metadata` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.fifo-subscriber-delivers-event-group-in-publish-order` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.horizon-subscriber-replays-retained-events-marked-replay` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.jsonata-transformer-reads-octet-stream-data-as-base64` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.jsonata-transformer-string-unquoted-and-object-serialized` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.put-events-content-based-deduplication-suppresses-resend` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.put-events-envelope-carries-resources-time-and-region` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.put-events-explicit-deduplication-id-suppresses-repeat` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.put-events-reserved-aws-source-refused-and-never-delivered` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.put-raw-events-invalid-json-and-open-format-refused-per-entry` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.raw-transformer-delivers-payload-as-sent-and-bytes-as-base64` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.sequence-numbers-increase-with-ingestion-and-match-delivery` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.sqs-message-attributes-evaluate-jsonata-and-keep-binary-literal` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.subscriber-filters-combine-with-and-across-scopes` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.subscriber-on-failure-configuration-writes-dead-letter-record-to-sqs` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.system-metadata-content-type-filter-routes-by-content-type` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.update-subscriber-replaces-filter-configuration-whole` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.update-subscriber-transformer-applies-to-later-events` | `4df2823` | not ported | EventBridge v2 |
| `eventbridgev2__iam__sqs.with-metadata-transformer-delivers-full-event-with-string-system-metadata` | `4df2823` | not ported | EventBridge v2 |

## behavioral/events

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `events.activate-deactivate-event-source-state-transition` | `4df2823` | ported | `Events/ActivateDeactivateEventSourceStateTransition.cs` |
| `events.activate-event-source-nonexistent-not-found` | `4df2823` | ported | `Events/ActivateEventSourceNonexistentNotFound.cs` |
| `events.api-destination-4xx-dead-letters-without-retry-while-5xx-is-retried-until-its-retry-attempts-are-used-up` | `4df2823` | ported | `Events/ApiDestination4xxDeadLettersWithoutRetryWhile5xxIsRetriedUntilItsRetryAttemptsAreUsedUp.cs` |
| `events.api-destination-api-key-connection-adds-its-key-and-parameters-and-its-values-win-over-the-targets` | `4df2823` | ported | `Events/ApiDestinationApiKeyConnectionAddsItsKeyAndParametersAndItsValuesWinOverTheTargets.cs` |
| `events.api-destination-basic-connection-sends-the-basic-authorization-header-with-the-destinations-method-and-the-bare-envelope` | `4df2823` | ported | `Events/ApiDestinationBasicConnectionSendsTheBasicAuthorizationHeaderWithTheDestinationsMethodAndTheBareEnvelope.cs` |
| `events.api-destination-invocation-rate-limit-maximum-is-enforced` | `4df2823` | ported | `Events/ApiDestinationInvocationRateLimitMaximumIsEnforced.cs` |
| `events.api-destination-lifecycle` | `4df2823` | ported | `Events/ApiDestinationLifecycle.cs` |
| `events.api-destination-oauth-connection-fetches-a-client-credentials-token-and-sends-it-as-the-bearer-header` | `4df2823` | ported | `Events/ApiDestinationOauthConnectionFetchesAClientCredentialsTokenAndSendsItAsTheBearerHeader.cs` |
| `events.api-destination-rate-limit-defers-a-burst-it-cannot-absorb-and-still-delivers-every-event` | `4df2823` | ported | `Events/ApiDestinationRateLimitDefersABurstItCannotAbsorbAndStillDeliversEveryEvent.cs` |
| `events.api-destination-state-follows-connection-deauthorize-and-reauthorize` | `4df2823` | ported | `Events/ApiDestinationStateFollowsConnectionDeauthorizeAndReauthorize.cs` |
| `events.archive-managed-rule-refuses-customer-writes-and-survives-them` | `4df2823` | ported | `Events/ArchiveManagedRuleRefusesCustomerWritesAndSurvivesThem.cs` |
| `events.bus-policy-role-statement-admits-put-events-but-account-statement-leaves-bare-role-denied` | `4df2823` | not ported | IAM role and identity-policy enforcement |
| `events.bus-target-without-role-arn-rejected-in-both-regions-and-the-rule-keeps-its-targets` | `4df2823` | ported | `Events/BusTargetWithoutRoleArnRejectedInBothRegionsAndTheRuleKeepsItsTargets.cs` |
| `events.cancel-replay-in-flight-reaches-cancelled-and-stays-terminal` | `4df2823` | ported | `Events/CancelReplayInFlightReachesCancelledAndStaysTerminal.cs` |
| `events.cmk-encrypted-bus-delivers-put-events-to-sqs-target` | `4df2823` | not ported | KMS keys |
| `events.cmk-without-events-grant-rejected-on-create-and-update` | `4df2823` | not ported | KMS keys |
| `events.concurrent-activate-event-source-conflict-detected` | `4df2823` | ported | `Events/ConcurrentActivateEventSourceConflictDetected.cs` |
| `events.concurrent-put-events-unique-event-ids` | `4df2823` | ported | `Events/ConcurrentPutEventsUniqueEventIds.cs` |
| `events.concurrent-update-endpoint-conflict-detected` | `4df2823` | ported | `Events/ConcurrentUpdateEndpointConflictDetected.cs` |
| `events.concurrent-update-event-bus-conflict-detected` | `4df2823` | ported | `Events/ConcurrentUpdateEventBusConflictDetected.cs` |
| `events.create-api-destination-accepts-non-url-endpoint` | `4df2823` | ported | `Events/CreateApiDestinationAcceptsNonUrlEndpoint.cs` |
| `events.create-api-destination-invalid-endpoint-pattern-rejected` | `4df2823` | ported | `Events/CreateApiDestinationInvalidEndpointPatternRejected.cs` |
| `events.create-api-destination-requires-existing-connection` | `4df2823` | ported | `Events/CreateApiDestinationRequiresExistingConnection.cs` |
| `events.create-archive-and-describe` | `4df2823` | ported | `Events/CreateArchiveAndDescribe.cs` |
| `events.create-archive-duplicate-fails` | `4df2823` | ported | `Events/CreateArchiveDuplicateFails.cs` |
| `events.create-archive-nonexistent-bus-fails` | `4df2823` | ported | `Events/CreateArchiveNonexistentBusFails.cs` |
| `events.create-connection-missing-auth-parameters-rejected` | `4df2823` | ported | `Events/CreateConnectionMissingAuthParametersRejected.cs` |
| `events.create-connection-state-transition` | `4df2823` | ported | `Events/CreateConnectionStateTransition.cs` |
| `events.create-duplicate-event-bus-fails` | `4df2823` | ported | `Events/CreateDuplicateEventBusFails.cs` |
| `events.create-endpoint-duplicate-name-blocked` | `4df2823` | ported | `Events/CreateEndpointDuplicateNameBlocked.cs` |
| `events.create-endpoint-malformed-routing-config-rejected` | `4df2823` | ported | `Events/CreateEndpointMalformedRoutingConfigRejected.cs` |
| `events.create-event-bus-basic` | `4df2823` | ported | `Events/CreateEventBusBasic.cs` |
| `events.create-event-bus-reserved-and-malformed-names-rejected` | `4df2823` | ported | `Events/CreateEventBusReservedAndMalformedNamesRejected.cs` |
| `events.cross-region-bus-target-delivers-to-the-other-regions-rules-with-the-source-region-and-a-new-event-id` | `4df2823` | ported | `Events/CrossRegionBusTargetDeliversToTheOtherRegionsRulesWithTheSourceRegionAndANewEventId.cs` |
| `events.custom-bus-addressed-by-arn-is-the-same-bus-as-by-name` | `4df2823` | ported | `Events/CustomBusAddressedByArnIsTheSameBusAsByName.cs` |
| `events.custom-bus-put-events-routes-each-batch-entry-by-its-detail-content` | `4df2823` | ported | `Events/CustomBusPutEventsRoutesEachBatchEntryByItsDetailContent.cs` |
| `events.custom-bus-rule-calls-without-event-bus-name-not-found` | `4df2823` | ported | `Events/CustomBusRuleCallsWithoutEventBusNameNotFound.cs` |
| `events.deactivate-event-source-nonexistent-not-found` | `4df2823` | ported | `Events/DeactivateEventSourceNonexistentNotFound.cs` |
| `events.deauthorize-connection-state-transition` | `4df2823` | ported | `Events/DeauthorizeConnectionStateTransition.cs` |
| `events.default-event-bus-listed` | `4df2823` | ported | `Events/DefaultEventBusListed.cs` |
| `events.delete-archive-not-blocked-by-in-flight-replay-and-start-replay-on-it-refused` | `4df2823` | ported | `Events/DeleteArchiveNotBlockedByInFlightReplayAndStartReplayOnItRefused.cs` |
| `events.delete-archive-removes-it` | `4df2823` | ported | `Events/DeleteArchiveRemovesIt.cs` |
| `events.delete-connection-eventually-not-found` | `4df2823` | ported | `Events/DeleteConnectionEventuallyNotFound.cs` |
| `events.delete-connection-in-use-by-api-destination-allowed` | `4df2823` | ported | `Events/DeleteConnectionInUseByApiDestinationAllowed.cs` |
| `events.delete-default-event-bus-protected` | `4df2823` | ported | `Events/DeleteDefaultEventBusProtected.cs` |
| `events.delete-endpoint-nonexistent-not-found` | `4df2823` | ported | `Events/DeleteEndpointNonexistentNotFound.cs` |
| `events.delete-event-bus-blocked-while-a-rule-remains-and-succeeds-once-removed` | `4df2823` | ported | `Events/DeleteEventBusBlockedWhileARuleRemainsAndSucceedsOnceRemoved.cs` |
| `events.delete-event-bus-blocked-while-an-archive-targets-it` | `4df2823` | ported | `Events/DeleteEventBusBlockedWhileAnArchiveTargetsIt.cs` |
| `events.delete-event-bus-removes-resource` | `4df2823` | ported | `Events/DeleteEventBusRemovesResource.cs` |
| `events.delete-nonexistent-rule-is-idempotent` | `4df2823` | ported | `Events/DeleteNonexistentRuleIsIdempotent.cs` |
| `events.delete-partner-event-source-disabled-operation-blocked` | `4df2823` | ported | `Events/DeletePartnerEventSourceDisabledOperationBlocked.cs` |
| `events.delete-rule-removes-it` | `4df2823` | ported | `Events/DeleteRuleRemovesIt.cs` |
| `events.delete-rule-with-targets-requires-force` | `4df2823` | ported | `Events/DeleteRuleWithTargetsRequiresForce.cs` |
| `events.delete-rule-without-event-bus-name-leaves-custom-bus-rule-in-place` | `4df2823` | ported | `Events/DeleteRuleWithoutEventBusNameLeavesCustomBusRuleInPlace.cs` |
| `events.deploy-cfn-bus-target-with-a-stack-created-execution-role-forwards-events-to-the-destination-bus` | `4df2823` | not ported | CloudFormation stacks |
| `events.deploy-cfn-event-bus-policy-admits-the-named-role-and-a-stack-update-removing-it-denies-again` | `4df2823` | not ported | CloudFormation stacks |
| `events.deploy-cfn-schedule-rule-fires-into-its-target-and-a-stack-update-disabling-it-stops-firing` | `4df2823` | not ported | CloudFormation stacks |
| `events.deploy-cfn-target-dead-letter-config-captures-undeliverable-events-until-a-stack-update-adds-the-queue-policy` | `4df2823` | not ported | CloudFormation stacks |
| `events.deploy-eventbus-rule-target-cfn-then-put-events-delivers` | `4df2823` | not ported | CloudFormation stacks |
| `events.describe-connection-redacts-secret-auth-material` | `4df2823` | ported | `Events/DescribeConnectionRedactsSecretAuthMaterial.cs` |
| `events.describe-endpoint-nonexistent-not-found` | `4df2823` | ported | `Events/DescribeEndpointNonexistentNotFound.cs` |
| `events.describe-event-source-nonexistent-not-found` | `4df2823` | ported | `Events/DescribeEventSourceNonexistentNotFound.cs` |
| `events.describe-nonexistent-event-bus-fails` | `4df2823` | ported | `Events/DescribeNonexistentEventBusFails.cs` |
| `events.describe-nonexistent-rule-fails` | `4df2823` | ported | `Events/DescribeNonexistentRuleFails.cs` |
| `events.describe-partner-event-source-nonexistent-not-found` | `4df2823` | ported | `Events/DescribePartnerEventSourceNonexistentNotFound.cs` |
| `events.describe-replay-nonexistent-not-found` | `4df2823` | ported | `Events/DescribeReplayNonexistentNotFound.cs` |
| `events.disabled-bus-key-stops-delivery-dead-letters-encrypted-events-until-reenabled` | `4df2823` | not ported | KMS keys |
| `events.disabled-rule-with-a-dead-letter-configured-target-drops-the-event-instead-of-dead-lettering-it` | `4df2823` | known gap: EventBridgeUngrantedTargets | `Events/DisabledRuleWithADeadLetterConfiguredTargetDropsTheEventInsteadOfDeadLetteringIt.cs` |
| `events.e2e-bus-rule-archive-replay-redelivers-event` | `4df2823` | ported | `Events/E2eBusRuleArchiveReplayRedeliversEvent.cs` |
| `events.e2e-partner-event-source-activation-to-ingestion-flow` | `4df2823` | ported | `Events/E2EPartnerEventSourceActivationToIngestionFlow.cs` |
| `events.enable-disable-rule-roundtrip` | `4df2823` | ported | `Events/EnableDisableRuleRoundtrip.cs` |
| `events.enable-nonexistent-rule-fails` | `4df2823` | ported | `Events/EnableNonexistentRuleFails.cs` |
| `events.endpoint-failover-routing-config-managed` | `4df2823` | ported | `Events/EndpointFailoverRoutingConfigManaged.cs` |
| `events.endpoint-lifecycle-create-active-delete-404` | `4df2823` | ported | `Events/EndpointLifecycleCreateActiveDelete404.cs` |
| `events.endpoint-routing-config-round-trip` | `4df2823` | ported | `Events/EndpointRoutingConfigRoundTrip.cs` |
| `events.event-bus-dead-letter-config-rejects-non-sqs-arns-and-keeps-prior-config` | `4df2823` | ported | `Events/EventBusDeadLetterConfigRejectsNonSqsArnsAndKeepsPriorConfig.cs` |
| `events.event-bus-tag-limit-counts-tags-already-on-the-bus-and-overwrites-do-not-count` | `4df2823` | ported | `Events/EventBusTagLimitCountsTagsAlreadyOnTheBusAndOverwritesDoNotCount.cs` |
| `events.event-pattern-over-2048-characters-rejected-counting-whitespace-and-the-rule-keeps-its-pattern` | `4df2823` | ported | `Events/EventPatternOver2048CharactersRejectedCountingWhitespaceAndTheRuleKeepsItsPattern.cs` |
| `events.events-archive-replay-filter-arn-naming-a-rule-on-another-bus-rejected` | `4df2823` | ported | `Events/EventsArchiveReplayFilterArnNamingARuleOnAnotherBusRejected.cs` |
| `events.events-archive-replay-to-a-bus-other-than-the-archive-source-rejected` | `4df2823` | ported | `Events/EventsArchiveReplayToABusOtherThanTheArchiveSourceRejected.cs` |
| `events.events-archive-stores-only-the-events-matching-its-event-pattern` | `4df2823` | ported | `Events/EventsArchiveStoresOnlyTheEventsMatchingItsEventPattern.cs` |
| `events.events-create-archive-with-an-invalid-event-pattern-rejected` | `4df2823` | ported | `Events/EventsCreateArchiveWithAnInvalidEventPatternRejected.cs` |
| `events.express-and-standard-state-machine-targets-start-with-the-envelope-as-input-named-after-the-event-id` | `4df2823` | not ported | Step Functions targets |
| `events.identity-explicit-deny-overrides-a-bus-policy-allow-naming-the-same-role` | `4df2823` | not ported | IAM role and identity-policy enforcement |
| `events.identity-policy-events-source-condition-denies-put-events-for-any-other-source` | `4df2823` | not ported | IAM role and identity-policy enforcement |
| `events.input-transformer-array-paths-render-elements-slices-and-null-exactly` | `4df2823` | ported | `Events/InputTransformerArrayPathsRenderElementsSlicesAndNullExactly.cs` |
| `events.input-transformer-plain-text-template-does-not-escape-strings-and-dead-letters-invalid-json` | `4df2823` | ported | `Events/InputTransformerPlainTextTemplateDoesNotEscapeStringsAndDeadLettersInvalidJson.cs` |
| `events.input-transformer-unsupported-input-path-forms-rejected-and-targets-unchanged` | `4df2823` | ported | `Events/InputTransformerUnsupportedInputPathFormsRejectedAndTargetsUnchanged.cs` |
| `events.kinesis-partition-key-path-renders-numbers-and-objects-as-json-and-an-absent-field-as-empty-object` | `4df2823` | not ported | Kinesis targets |
| `events.lambda-target-function-error-is-retried-by-lambda-and-never-dead-lettered-by-the-rule` | `4df2823` | not ported | Lambda targets |
| `events.lambda-target-is-invoked-asynchronously-and-its-on-success-destination-records-the-envelope` | `4df2823` | not ported | Lambda targets |
| `events.list-archives-filters-by-prefix-and-source` | `4df2823` | ported | `Events/ListArchivesFiltersByPrefixAndSource.cs` |
| `events.list-connections-filters-are-mutually-exclusive` | `4df2823` | ported | `Events/ListConnectionsFiltersAreMutuallyExclusive.cs` |
| `events.list-event-buses-includes-default-and-custom` | `4df2823` | ported | `Events/ListEventBusesIncludesDefaultAndCustom.cs` |
| `events.list-event-buses-paginates-with-limit-and-next-token` | `4df2823` | ported | `Events/ListEventBusesPaginatesWithLimitAndNextToken.cs` |
| `events.list-partner-event-source-accounts-unknown-source-not-found` | `4df2823` | ported | `Events/ListPartnerEventSourceAccountsUnknownSourceNotFound.cs` |
| `events.list-partner-event-sources-disabled-account-denied` | `4df2823` | ported | `Events/ListPartnerEventSourcesDisabledAccountDenied.cs` |
| `events.list-replays-combined-filters-rejected` | `4df2823` | ported | `Events/ListReplaysCombinedFiltersRejected.cs` |
| `events.list-rule-names-by-target` | `4df2823` | ported | `Events/ListRuleNamesByTarget.cs` |
| `events.list-rule-names-by-target-paginates-with-limit-and-next-token` | `4df2823` | ported | `Events/ListRuleNamesByTargetPaginatesWithLimitAndNextToken.cs` |
| `events.list-rules-pagination-and-name-prefix-exactness` | `4df2823` | ported | `Events/ListRulesPaginationAndNamePrefixExactness.cs` |
| `events.list-targets-by-rule-paginates-with-limit-and-next-token` | `4df2823` | ported | `Events/ListTargetsByRulePaginatesWithLimitAndNextToken.cs` |
| `events.log-group-target-dead-letters-events-timed-too-old-or-too-far-ahead-while-a-current-event-is-written` | `4df2823` | not ported | CloudWatch Logs targets |
| `events.nested-or-inside-detail-agrees-between-test-event-pattern-and-real-delivery` | `4df2823` | ported | `Events/NestedOrInsideDetailAgreesBetweenTestEventPatternAndRealDelivery.cs` |
| `events.oauth-connection-non-https-authorization-endpoint-rejected` | `4df2823` | ported | `Events/OauthConnectionNonHttpsAuthorizationEndpointRejected.cs` |
| `events.oauth-connection-with-unreachable-endpoint-deauthorizes-and-its-destination-is-inactive` | `4df2823` | ported | `Events/OauthConnectionWithUnreachableEndpointDeauthorizesAndItsDestinationIsInactive.cs` |
| `events.partner-event-source-lifecycle-create-rejected-non-partner` | `4df2823` | ported | `Events/PartnerEventSourceLifecycleCreateRejectedNonPartner.cs` |
| `events.policy-scoped-to-one-bus-rules-denies-put-targets-on-the-same-rule-name-on-another-bus` | `4df2823` | not ported | IAM role and identity-policy enforcement |
| `events.put-and-remove-permission-conflicting-parameters-rejected` | `4df2823` | ported | `Events/PutAndRemovePermissionConflictingParametersRejected.cs` |
| `events.put-events-detail-json-nesting-depth-limit` | `4df2823` | ported | `Events/PutEventsDetailJsonNestingDepthLimit.cs` |
| `events.put-events-detail-that-is-json-but-not-an-object-rejected` | `4df2823` | ported | `Events/PutEventsDetailThatIsJsonButNotAnObjectRejected.cs` |
| `events.put-events-endpoint-id-delivers-to-primary-and-replicates-to-secondary` | `4df2823` | ported | `Events/PutEventsEndpointIdDeliversToPrimaryAndReplicatesToSecondary.cs` |
| `events.put-events-endpoint-id-with-unrelated-event-bus-name-rejected` | `4df2823` | ported | `Events/PutEventsEndpointIdWithUnrelatedEventBusNameRejected.cs` |
| `events.put-events-malformed-detail-entry-fails` | `4df2823` | ported | `Events/PutEventsMalformedDetailEntryFails.cs` |
| `events.put-events-missing-source-entry-fails` | `4df2823` | ported | `Events/PutEventsMissingSourceEntryFails.cs` |
| `events.put-events-nonexistent-bus-silently-dropped` | `4df2823` | ported | `Events/PutEventsNonexistentBusSilentlyDropped.cs` |
| `events.put-events-numeric-precision-preserved` | `4df2823` | ported | `Events/PutEventsNumericPrecisionPreserved.cs` |
| `events.put-events-one-mebibyte-request-limit-refused-whole-and-large-event-delivers-intact` | `4df2823` | ported | `Events/PutEventsOneMebibyteRequestLimitRefusedWholeAndLargeEventDeliversIntact.cs` |
| `events.put-events-partial-failure-invalid-entry` | `4df2823` | ported | `Events/PutEventsPartialFailureInvalidEntry.cs` |
| `events.put-events-partial-failure-positional-alignment` | `4df2823` | ported | `Events/PutEventsPartialFailurePositionalAlignment.cs` |
| `events.put-events-target-without-resource-policy-blocks-delivery` | `4df2823` | known gap: EventBridgeUngrantedTargets | `Events/PutEventsTargetWithoutResourcePolicyBlocksDelivery.cs` |
| `events.put-events-trace-header-reaches-sqs-targets-as-aws-trace-header` | `4df2823` | ported | `Events/PutEventsTraceHeaderReachesSqsTargetsAsAwsTraceHeader.cs` |
| `events.put-events-triggers-matched-target-delivery` | `4df2823` | ported | `Events/PutEventsTriggersMatchedTargetDelivery.cs` |
| `events.put-events-unicode-detail-round-trip` | `4df2823` | ported | `Events/PutEventsUnicodeDetailRoundTrip.cs` |
| `events.put-events-unmatched-entry-not-delivered` | `4df2823` | ported | `Events/PutEventsUnmatchedEntryNotDelivered.cs` |
| `events.put-events-valid-returns-event-id` | `4df2823` | ported | `Events/PutEventsValidReturnsEventId.cs` |
| `events.put-partner-events-non-partner-account-denied` | `4df2823` | ported | `Events/PutPartnerEventsNonPartnerAccountDenied.cs` |
| `events.put-permission-same-statement-id-replaces-and-remove-all-clears-policy` | `4df2823` | ported | `Events/PutPermissionSameStatementIdReplacesAndRemoveAllClearsPolicy.cs` |
| `events.put-remove-permission-roundtrip` | `4df2823` | ported | `Events/PutRemovePermissionRoundtrip.cs` |
| `events.put-rule-default-state-enabled` | `4df2823` | ported | `Events/PutRuleDefaultStateEnabled.cs` |
| `events.put-rule-invalid-pattern-fails` | `4df2823` | ported | `Events/PutRuleInvalidPatternFails.cs` |
| `events.put-rule-invalid-schedule-expression-rejected` | `4df2823` | ported | `Events/PutRuleInvalidScheduleExpressionRejected.cs` |
| `events.put-rule-new-pattern-stops-old-matches-and-keeps-targets` | `4df2823` | ported | `Events/PutRuleNewPatternStopsOldMatchesAndKeepsTargets.cs` |
| `events.put-rule-nonexistent-bus-fails` | `4df2823` | ported | `Events/PutRuleNonexistentBusFails.cs` |
| `events.put-rule-schedule-expression-grammar-rejection` | `4df2823` | ported | `Events/PutRuleScheduleExpressionGrammarRejection.cs` |
| `events.put-rule-update-replaces-omitted-fields-and-ignores-tags` | `4df2823` | ported | `Events/PutRuleUpdateReplacesOmittedFieldsAndIgnoresTags.cs` |
| `events.put-rule-without-pattern-or-schedule-rejected` | `4df2823` | ported | `Events/PutRuleWithoutPatternOrScheduleRejected.cs` |
| `events.put-target-role-retry-policy-and-dead-letter-config-roundtrip-then-role-replaced` | `4df2823` | ported | `Events/PutTargetRoleRetryPolicyAndDeadLetterConfigRoundtripThenRoleReplaced.cs` |
| `events.put-targets-and-list` | `4df2823` | ported | `Events/PutTargetsAndList.cs` |
| `events.put-targets-duplicate-id-in-one-request-rejected` | `4df2823` | ported | `Events/PutTargetsDuplicateIdInOneRequestRejected.cs` |
| `events.put-targets-exceeds-max-limit` | `4df2823` | ported | `Events/PutTargetsExceedsMaxLimit.cs` |
| `events.put-targets-fifo-sqs-dead-letter-config-rejected` | `4df2823` | ported | `Events/PutTargetsFifoSqsDeadLetterConfigRejected.cs` |
| `events.put-targets-malformed-arn-rejected-atomically` | `4df2823` | ported | `Events/PutTargetsMalformedArnRejectedAtomically.cs` |
| `events.put-targets-new-id-on-full-rule-rejected-while-same-id-upsert-accepted` | `4df2823` | ported | `Events/PutTargetsNewIdOnFullRuleRejectedWhileSameIdUpsertAccepted.cs` |
| `events.put-targets-nonexistent-rule-fails` | `4df2823` | ported | `Events/PutTargetsNonexistentRuleFails.cs` |
| `events.put-targets-omitted-role-arn-rejected-leaves-prior-target-unchanged` | `4df2823` | ported | `Events/PutTargetsOmittedRoleArnRejectedLeavesPriorTargetUnchanged.cs` |
| `events.put-targets-retry-policy-above-documented-maxima-rejected` | `4df2823` | ported | `Events/PutTargetsRetryPolicyAboveDocumentedMaximaRejected.cs` |
| `events.put-targets-same-id-new-arn-moves-delivery-to-the-new-queue` | `4df2823` | ported | `Events/PutTargetsSameIdNewArnMovesDeliveryToTheNewQueue.cs` |
| `events.put-targets-sns-fifo-topic-target-rejected` | `4df2823` | ported | `Events/PutTargetsSnsFifoTopicTargetRejected.cs` |
| `events.put-targets-with-role-arn-denied-without-iam-pass-role-until-granted` | `4df2823` | not ported | IAM role and identity-policy enforcement |
| `events.queue-policy-deny-and-missing-allow-both-dead-letter-as-no-resource-without-retries` | `4df2823` | known gap: EventBridgeUngrantedTargets | `Events/QueuePolicyDenyAndMissingAllowBothDeadLetterAsNoResourceWithoutRetries.cs` |
| `events.recreated-event-bus-inherits-no-tags-policy-or-description` | `4df2823` | ported | `Events/RecreatedEventBusInheritsNoTagsPolicyOrDescription.cs` |
| `events.remove-permission-nonexistent-fails` | `4df2823` | ported | `Events/RemovePermissionNonexistentFails.cs` |
| `events.remove-targets-removes-them` | `4df2823` | ported | `Events/RemoveTargetsRemovesThem.cs` |
| `events.remove-targets-unknown-id-is-lenient` | `4df2823` | ported | `Events/RemoveTargetsUnknownIdIsLenient.cs` |
| `events.replay-completion-populates-timestamps-and-list-state-filter-discriminates` | `4df2823` | ported | `Events/ReplayCompletionPopulatesTimestampsAndListStateFilterDiscriminates.cs` |
| `events.replay-reaches-completed` | `4df2823` | ported | `Events/ReplayReachesCompleted.cs` |
| `events.resource-tag-condition-scopes-disable-rule-to-rules-carrying-the-tag` | `4df2823` | not ported | IAM role and identity-policy enforcement |
| `events.rule-on-the-envelope-account-and-region-routes-only-this-accounts-events-here` | `4df2823` | ported | `Events/RuleOnTheEnvelopeAccountAndRegionRoutesOnlyThisAccountsEventsHere.cs` |
| `events.rule-scoped-to-event-bus-isolation` | `4df2823` | ported | `Events/RuleScopedToEventBusIsolation.cs` |
| `events.schedule-rule-on-custom-bus-rejected` | `4df2823` | ported | `Events/ScheduleRuleOnCustomBusRejected.cs` |
| `events.schedule-rule-on-default-bus-accepted` | `4df2823` | ported | `Events/ScheduleRuleOnDefaultBusAccepted.cs` |
| `events.sns-target-on-a-kms-encrypted-topic-dead-letters-as-no-permissions-until-the-key-policy-grants-events` | `4df2823` | not ported | KMS keys |
| `events.sns-target-publishes-the-compact-envelope-and-a-plain-text-template-keeps-its-quotes` | `4df2823` | ported | `Events/SnsTargetPublishesTheCompactEnvelopeAndAPlainTextTemplateKeepsItsQuotes.cs` |
| `events.sns-target-without-topic-policy-dead-letters-as-access-denied-until-the-policy-grants-publish` | `4df2823` | known gap: EventBridgeUngrantedTargets | `Events/SnsTargetWithoutTopicPolicyDeadLettersAsAccessDeniedUntilThePolicyGrantsPublish.cs` |
| `events.sqs-target-encrypted-with-a-key-events-cannot-use-dead-letters-until-the-key-policy-grants-it` | `4df2823` | not ported | KMS keys |
| `events.sqs-target-message-group-id-json-path-resolves-per-event` | `4df2823` | ported | `Events/SqsTargetMessageGroupIdJsonPathResolvesPerEvent.cs` |
| `events.start-replay-duplicate-name-rejected` | `4df2823` | ported | `Events/StartReplayDuplicateNameRejected.cs` |
| `events.start-replay-end-time-before-start-rejected` | `4df2823` | ported | `Events/StartReplayEndTimeBeforeStartRejected.cs` |
| `events.start-replay-invalid-source-rejected` | `4df2823` | ported | `Events/StartReplayInvalidSourceRejected.cs` |
| `events.system-tag-keys-rejected-on-event-bus-and-tags-unchanged` | `4df2823` | ported | `Events/SystemTagKeysRejectedOnEventBusAndTagsUnchanged.cs` |
| `events.tag-list-untag-roundtrip` | `4df2823` | ported | `Events/TagListUntagRoundtrip.cs` |
| `events.tag-nonexistent-resource-fails` | `4df2823` | ported | `Events/TagNonexistentResourceFails.cs` |
| `events.test-event-pattern-anything-but-exclusion-forms` | `4df2823` | ported | `Events/TestEventPatternAnythingButExclusionForms.cs` |
| `events.test-event-pattern-anything-but-mixed-string-number-list-rejected` | `4df2823` | ported | `Events/TestEventPatternAnythingButMixedStringNumberListRejected.cs` |
| `events.test-event-pattern-anything-but-nested-suffix-equals-ignore-case-and-wildcard-forms` | `4df2823` | ported | `Events/TestEventPatternAnythingButNestedSuffixEqualsIgnoreCaseAndWildcardForms.cs` |
| `events.test-event-pattern-anything-but-over-array-valued-field-admits-any-escaping-element` | `4df2823` | ported | `Events/TestEventPatternAnythingButOverArrayValuedFieldAdmitsAnyEscapingElement.cs` |
| `events.test-event-pattern-cidr-matcher-boundaries` | `4df2823` | ported | `Events/TestEventPatternCidrMatcherBoundaries.cs` |
| `events.test-event-pattern-envelope-required-fields-and-parseable-time` | `4df2823` | ported | `Events/TestEventPatternEnvelopeRequiredFieldsAndParseableTime.cs` |
| `events.test-event-pattern-exact-value-case-and-type-sensitivity` | `4df2823` | ported | `Events/TestEventPatternExactValueCaseAndTypeSensitivity.cs` |
| `events.test-event-pattern-exists-and-null-vs-empty-string` | `4df2823` | ported | `Events/TestEventPatternExistsAndNullVsEmptyString.cs` |
| `events.test-event-pattern-exists-false-matches-when-an-intermediate-parent-is-missing` | `4df2823` | ported | `Events/TestEventPatternExistsFalseMatchesWhenAnIntermediateParentIsMissing.cs` |
| `events.test-event-pattern-exists-treats-every-object-leaf-and-the-empty-array-as-absent` | `4df2823` | ported | `Events/TestEventPatternExistsTreatsEveryObjectLeafAndTheEmptyArrayAsAbsent.cs` |
| `events.test-event-pattern-invalid-fails` | `4df2823` | ported | `Events/TestEventPatternInvalidFails.cs` |
| `events.test-event-pattern-malformed-operator-rejection` | `4df2823` | ported | `Events/TestEventPatternMalformedOperatorRejection.cs` |
| `events.test-event-pattern-match` | `4df2823` | ported | `Events/TestEventPatternMatch.cs` |
| `events.test-event-pattern-match-and-mismatch` | `4df2823` | ported | `Events/TestEventPatternMatchAndMismatch.cs` |
| `events.test-event-pattern-multiple-filters-in-one-list-are-or-joined` | `4df2823` | ported | `Events/TestEventPatternMultipleFiltersInOneListAreOrJoined.cs` |
| `events.test-event-pattern-numeric-range-boundaries` | `4df2823` | ported | `Events/TestEventPatternNumericRangeBoundaries.cs` |
| `events.test-event-pattern-numeric-rejects-non-numeric-json-values-and-degenerate-ranges` | `4df2823` | ported | `Events/TestEventPatternNumericRejectsNonNumericJsonValuesAndDegenerateRanges.cs` |
| `events.test-event-pattern-or-and-semantics-across-keys-and-arrays` | `4df2823` | ported | `Events/TestEventPatternOrAndSemanticsAcrossKeysAndArrays.cs` |
| `events.test-event-pattern-string-content-filters` | `4df2823` | ported | `Events/TestEventPatternStringContentFilters.cs` |
| `events.twelve-rules-matching-one-event-each-deliver-their-own-copy-and-a-near-miss-rule-none` | `4df2823` | ported | `Events/TwelveRulesMatchingOneEventEachDeliverTheirOwnCopyAndANearMissRuleNone.cs` |
| `events.update-archive-event-pattern-governs-what-is-archived-afterwards` | `4df2823` | ported | `Events/UpdateArchiveEventPatternGovernsWhatIsArchivedAfterwards.cs` |
| `events.update-archive-pattern-is-mirrored-into-its-managed-rule-and-delete-removes-it` | `4df2823` | ported | `Events/UpdateArchivePatternIsMirroredIntoItsManagedRuleAndDeleteRemovesIt.cs` |
| `events.update-connection-authorization-type` | `4df2823` | ported | `Events/UpdateConnectionAuthorizationType.cs` |
| `events.update-endpoint-changes-routing-config` | `4df2823` | ported | `Events/UpdateEndpointChangesRoutingConfig.cs` |
| `events.update-endpoint-nonexistent-not-found` | `4df2823` | ported | `Events/UpdateEndpointNonexistentNotFound.cs` |
| `events.update-event-bus-partial-update-agrees-across-every-read-surface` | `4df2823` | ported | `Events/UpdateEventBusPartialUpdateAgreesAcrossEveryReadSurface.cs` |
| `events.update-event-bus-policy-enforced-on-put-events` | `4df2823` | ported | `Events/UpdateEventBusPolicyEnforcedOnPutEvents.cs` |

## behavioral/events__iam__lambda__sqs

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `events__iam__lambda__sqs.eventbridge-rule-no-permission-to-invoke-lambda` | `4df2823` | not ported | Lambda targets |

## behavioral/events__iam__sns__sqs__sts

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `events__iam__sns__sqs__sts.put-targets-target-arn-condition-restricts-to-sqs` | `4df2823` | not ported | IAM role and identity-policy enforcement |

## behavioral/events__iam__sqs__stepfunctions

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `events__iam__sqs__stepfunctions.xsvc-iam-3-events-role-starts-sfn-execution-with-the-event-as-input` | `4df2823` | not ported | Step Functions targets |
| `events__iam__sqs__stepfunctions.xsvc-iam-3-role-without-events-trust-rejected-at-delivery-failed-to-assume-role` | `4df2823` | not ported | Step Functions targets |
| `events__iam__sqs__stepfunctions.xsvc-iam-3-role-without-start-execution-denied-dead-letters-and-counts-failed-invocations` | `4df2823` | not ported | Step Functions targets |

## behavioral/events__iam__sts

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `events__iam__sts.put-rule-source-conditions-gate-the-event-pattern` | `4df2823` | not ported | IAM role and identity-policy enforcement |

## behavioral/events__sqs

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `events__sqs.api-destination-timeout-retries-are-bounded-by-maximum-retry-attempts` | `4df2823` | ported | `EventsSqs/ApiDestinationTimeoutRetriesAreBoundedByMaximumRetryAttempts.cs` |
| `events__sqs.bus-to-bus-forwarding-happens-once-and-preserves-the-envelope` | `4df2823` | ported | `EventsSqs/BusToBusForwardingHappensOnceAndPreservesTheEnvelope.cs` |
| `events__sqs.custom-bus-fanout-is-at-least-once-and-order-independent` | `4df2823` | ported | `EventsSqs/CustomBusFanoutIsAtLeastOnceAndOrderIndependent.cs` |
| `events__sqs.enable-rule-direct-call-resumes-delivery` | `4df2823` | ported | `EventsSqs/EnableRuleDirectCallResumesDelivery.cs` |
| `events__sqs.eventbridge-anything-but-matcher-excludes-not-delivered` | `4df2823` | ported | `EventsSqs/EventbridgeAnythingButMatcherExcludesNotDelivered.cs` |
| `events__sqs.eventbridge-disabled-rule-not-delivered` | `4df2823` | ported | `EventsSqs/EventbridgeDisabledRuleNotDelivered.cs` |
| `events__sqs.eventbridge-input-transformer-missing-path-not-delivered` | `4df2823` | ported | `EventsSqs/EventbridgeInputTransformerMissingPathNotDelivered.cs` |
| `events__sqs.eventbridge-numeric-matcher-below-threshold-not-delivered` | `4df2823` | ported | `EventsSqs/EventbridgeNumericMatcherBelowThresholdNotDelivered.cs` |
| `events__sqs.eventbridge-putevents-partial-batch-delivers-valid-entry-only` | `4df2823` | ported | `EventsSqs/EventbridgePuteventsPartialBatchDeliversValidEntryOnly.cs` |
| `events__sqs.eventbridge-wrong-event-bus-not-delivered` | `4df2823` | ported | `EventsSqs/EventbridgeWrongEventBusNotDelivered.cs` |
| `events__sqs.events-archive-replay-does-not-redeliver-to-a-rule-outside-filter-arns` | `4df2823` | ported | `EventsSqs/EventsArchiveReplayDoesNotRedeliverToARuleOutsideFilterArns.cs` |
| `events__sqs.events-archive-replay-reaches-a-rule-created-after-the-events-were-archived` | `4df2823` | ported | `EventsSqs/EventsArchiveReplayReachesARuleCreatedAfterTheEventsWereArchived.cs` |
| `events__sqs.events-archive-replay-rule-matching-replay-name-exists-false-is-not-redelivered` | `4df2823` | known gap: EmulatorBudget | `EventsSqs/EventsArchiveReplayRuleMatchingReplayNameExistsFalseIsNotRedelivered.cs` |
| `events__sqs.events-bus-policy-explicit-deny-rejects-entire-putevents-batch` | `4df2823` | ported | `EventsSqs/EventsBusPolicyExplicitDenyRejectsEntirePuteventsBatch.cs` |
| `events__sqs.events-concurrent-putevents-every-event-reaches-the-target` | `4df2823` | ported | `EventsSqs/EventsConcurrentPuteventsEveryEventReachesTheTarget.cs` |
| `events__sqs.events-delivery-and-failure-conflicting-input-and-input-path-rejected` | `4df2823` | ported | `EventsSqs/EventsDeliveryAndFailureConflictingInputAndInputPathRejected.cs` |
| `events__sqs.events-delivery-and-failure-deleted-queue-target-dead-letters-no-resource-while-sibling-delivers` | `4df2823` | ported | `EventsSqs/EventsDeliveryAndFailureDeletedQueueTargetDeadLettersNoResourceWhileSiblingDelivers.cs` |
| `events__sqs.events-delivery-and-failure-input-transformer-reserved-variables-resolve-to-rule-and-event` | `4df2823` | ported | `EventsSqs/EventsDeliveryAndFailureInputTransformerReservedVariablesResolveToRuleAndEvent.cs` |
| `events__sqs.events-delivery-and-failure-input-transformer-substitution-forms-render-exactly` | `4df2823` | ported | `EventsSqs/EventsDeliveryAndFailureInputTransformerSubstitutionFormsRenderExactly.cs` |
| `events__sqs.events-delivery-and-failure-object-in-quoted-template-string-dead-letters-as-invalid-json` | `4df2823` | ported | `EventsSqs/EventsDeliveryAndFailureObjectInQuotedTemplateStringDeadLettersAsInvalidJson.cs` |
| `events__sqs.events-delivery-and-failure-static-input-and-input-path-replace-the-event` | `4df2823` | ported | `EventsSqs/EventsDeliveryAndFailureStaticInputAndInputPathReplaceTheEvent.cs` |
| `events__sqs.events-delivery-and-failure-unquoted-plain-text-template-rejected` | `4df2823` | ported | `EventsSqs/EventsDeliveryAndFailureUnquotedPlainTextTemplateRejected.cs` |
| `events__sqs.events-pattern-language-anything-but-excludes-values-and-never-matches-absence` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageAnythingButExcludesValuesAndNeverMatchesAbsence.cs` |
| `events__sqs.events-pattern-language-array-field-matches-if-any-element-matches` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageArrayFieldMatchesIfAnyElementMatches.cs` |
| `events__sqs.events-pattern-language-cidr-matches-addresses-inside-the-block-only` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageCidrMatchesAddressesInsideTheBlockOnly.cs` |
| `events__sqs.events-pattern-language-content-filter-combined-with-a-second-key-rejected` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageContentFilterCombinedWithASecondKeyRejected.cs` |
| `events__sqs.events-pattern-language-equals-ignore-case-folds-case-but-not-length` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageEqualsIgnoreCaseFoldsCaseButNotLength.cs` |
| `events__sqs.events-pattern-language-exists-false-does-not-match-a-present-null-field` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageExistsFalseDoesNotMatchAPresentNullField.cs` |
| `events__sqs.events-pattern-language-exists-tests-leaf-presence-not-truthiness` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageExistsTestsLeafPresenceNotTruthiness.cs` |
| `events__sqs.events-pattern-language-malformed-pattern-update-rejected-and-live-rule-keeps-matching` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageMalformedPatternUpdateRejectedAndLiveRuleKeepsMatching.cs` |
| `events__sqs.events-pattern-language-nested-object-pattern-matches-the-leaf-path` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageNestedObjectPatternMatchesTheLeafPath.cs` |
| `events__sqs.events-pattern-language-numeric-comparators-and-range-honour-their-boundaries` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageNumericComparatorsAndRangeHonourTheirBoundaries.cs` |
| `events__sqs.events-pattern-language-pattern-value-list-is-an-or` | `4df2823` | ported | `EventsSqs/EventsPatternLanguagePatternValueListIsAnOr.cs` |
| `events__sqs.events-pattern-language-prefix-matches-only-a-leading-substring` | `4df2823` | ported | `EventsSqs/EventsPatternLanguagePrefixMatchesOnlyALeadingSubstring.cs` |
| `events__sqs.events-pattern-language-suffix-matches-only-a-trailing-substring` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageSuffixMatchesOnlyATrailingSubstring.cs` |
| `events__sqs.events-pattern-language-top-level-or-matches-any-branch-and-ands-with-siblings` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageTopLevelOrMatchesAnyBranchAndAndsWithSiblings.cs` |
| `events__sqs.events-pattern-language-two-keys-in-one-object-are-an-and` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageTwoKeysInOneObjectAreAnAnd.cs` |
| `events__sqs.events-pattern-language-wildcard-star-spans-any-run-including-slashes` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageWildcardStarSpansAnyRunIncludingSlashes.cs` |
| `events__sqs.events-pattern-language-wrong-type-field-does-not-match-and-does-not-error` | `4df2823` | ported | `EventsSqs/EventsPatternLanguageWrongTypeFieldDoesNotMatchAndDoesNotError.cs` |
| `events__sqs.events-rate-schedule-fires-repeatedly-with-the-configured-period` | `4df2823` | ported | `EventsSqs/EventsRateScheduleFiresRepeatedlyWithTheConfiguredPeriod.cs` |
| `events__sqs.events-replay-redelivers-only-the-archived-events-inside-the-window` | `4df2823` | ported | `EventsSqs/EventsReplayRedeliversOnlyTheArchivedEventsInsideTheWindow.cs` |
| `events__sqs.events-test-event-pattern-verdicts-agree-with-real-delivery` | `4df2823` | ported | `EventsSqs/EventsTestEventPatternVerdictsAgreeWithRealDelivery.cs` |
| `events__sqs.non-matching-event-pattern-delivers-nothing-to-its-target` | `4df2823` | ported | `EventsSqs/NonMatchingEventPatternDeliversNothingToItsTarget.cs` |
| `events__sqs.put-events-batch-entries-route-each-to-its-own-bus` | `4df2823` | ported | `EventsSqs/PutEventsBatchEntriesRouteEachToItsOwnBus.cs` |
| `events__sqs.put-events-request-size-accounting-is-additive-and-time-costs-fourteen-bytes` | `4df2823` | ported | `EventsSqs/PutEventsRequestSizeAccountingIsAdditiveAndTimeCostsFourteenBytes.cs` |
| `events__sqs.put-events-time-resources-and-event-id-carried-into-the-delivered-envelope` | `4df2823` | ported | `EventsSqs/PutEventsTimeResourcesAndEventIdCarriedIntoTheDeliveredEnvelope.cs` |
| `events__sqs.put-events-trace-header-into-archived-bus-absent-on-replay` | `4df2823` | ported | `EventsSqs/PutEventsTraceHeaderIntoArchivedBusAbsentOnReplay.cs` |
| `events__sqs.put-rule-pattern-update-reroutes-delivery-and-keeps-targets` | `4df2823` | ported | `EventsSqs/PutRulePatternUpdateReroutesDeliveryAndKeepsTargets.cs` |
| `events__sqs.put-rule-update-omitting-state-re-enables-a-disabled-rule` | `4df2823` | ported | `EventsSqs/PutRuleUpdateOmittingStateReEnablesADisabledRule.cs` |
| `events__sqs.put-targets-same-id-upsert-moves-delivery-to-the-new-queue` | `4df2823` | ported | `EventsSqs/PutTargetsSameIdUpsertMovesDeliveryToTheNewQueue.cs` |
| `events__sqs.put-targets-upsert-with-invalid-input-rejected-and-live-target-keeps-delivering` | `4df2823` | ported | `EventsSqs/PutTargetsUpsertWithInvalidInputRejectedAndLiveTargetKeepsDelivering.cs` |
| `events__sqs.remove-one-of-two-targets-stops-only-its-delivery` | `4df2823` | ported | `EventsSqs/RemoveOneOfTwoTargetsStopsOnlyItsDelivery.cs` |
| `events__sqs.replay-redelivers-each-archived-event-exactly-once-and-never-the-out-of-window-one` | `4df2823` | ported | `EventsSqs/ReplayRedeliversEachArchivedEventExactlyOnceAndNeverTheOutOfWindowOne.cs` |
| `events__sqs.throttled-lambda-target-is-one-successful-attempt-with-no-retry-and-no-dlq` | `4df2823` | not ported | Lambda targets |

## behavioral/sns

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `sns.add-permission-invalid-action-rejected` | `4df2823` | ported | `Sns/AddPermissionInvalidActionRejected.cs` |
| `sns.add-permission-lifecycle-and-duplicate-label` | `4df2823` | ported | `Sns/AddPermissionLifecycleAndDuplicateLabel.cs` |
| `sns.add-permission-malformed-account-id-rejected` | `4df2823` | ported | `Sns/AddPermissionMalformedAccountIdRejected.cs` |
| `sns.add-permission-merges-into-custom-policy` | `4df2823` | ported | `Sns/AddPermissionMergesIntoCustomPolicy.cs` |
| `sns.add-permission-nonexistent-topic-fails` | `4df2823` | ported | `Sns/AddPermissionNonexistentTopicFails.cs` |
| `sns.authenticate-on-unsubscribe-refuses-the-anonymous-unsubscribe-url` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.authorization-error-explicit-deny-returns-correct-code` | `4df2823` | not ported | IAM roles and role assumption |
| `sns.check-phone-number-opted-out-invalid-fails` | `4df2823` | ported | `Sns/CheckPhoneNumberOptedOutInvalidFails.cs` |
| `sns.clearing-a-filter-policy-with-an-empty-value-restores-unfiltered-delivery` | `4df2823` | ported | `Sns/ClearingAFilterPolicyWithAnEmptyValueRestoresUnfilteredDelivery.cs` |
| `sns.concurrent-add-permission-distinct-labels-all-retained` | `4df2823` | ported | `Sns/ConcurrentAddPermissionDistinctLabelsAllRetained.cs` |
| `sns.concurrent-create-topic-same-name-converges` | `4df2823` | ported | `Sns/ConcurrentCreateTopicSameNameConverges.cs` |
| `sns.concurrent-tag-resource-distinct-keys-all-retained` | `4df2823` | ported | `Sns/ConcurrentTagResourceDistinctKeysAllRetained.cs` |
| `sns.confirm-subscription-token-confirms-only-its-own-subscription-whatever-the-topic-arn` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.confirm-subscription-token-is-reusable-until-unsubscribe-then-not-found` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.confirmed-sqs-subscription-attributes-and-listing-agree-field-for-field` | `4df2823` | ported | `Sns/ConfirmedSqsSubscriptionAttributesAndListingAgreeFieldForField.cs` |
| `sns.create-platform-application-unknown-platform-fails` | `4df2823` | ported | `Sns/CreatePlatformApplicationUnknownPlatformFails.cs` |
| `sns.create-platform-endpoint-nonexistent-app-fails` | `4df2823` | ported | `Sns/CreatePlatformEndpointNonexistentAppFails.cs` |
| `sns.create-sms-sandbox-invalid-number-rejected` | `4df2823` | ported | `Sns/CreateSmsSandboxInvalidNumberRejected.cs` |
| `sns.create-sms-sandbox-unsupported-country-code-rejected` | `4df2823` | ported | `Sns/CreateSmsSandboxUnsupportedCountryCodeRejected.cs` |
| `sns.create-topic-idempotent-and-conflict` | `4df2823` | ported | `Sns/CreateTopicIdempotentAndConflict.cs` |
| `sns.create-topic-invalid-attribute-creates-nothing` | `4df2823` | ported | `Sns/CreateTopicInvalidAttributeCreatesNothing.cs` |
| `sns.create-topic-with-different-tags-rejected` | `4df2823` | ported | `Sns/CreateTopicWithDifferentTagsRejected.cs` |
| `sns.cross-region-lambda-subscription-invokes-the-function-in-its-own-region` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.cross-region-sqs-subscription-delivers-with-the-topic-region-envelope` | `4df2823` | ported | `Sns/CrossRegionSqsSubscriptionDeliversWithTheTopicRegionEnvelope.cs` |
| `sns.custom-attribute-type-labels-and-string-array-survive-both-delivery-modes` | `4df2823` | ported | `Sns/CustomAttributeTypeLabelsAndStringArraySurviveBothDeliveryModes.cs` |
| `sns.delete-endpoint-nonexistent-idempotent` | `4df2823` | ported | `Sns/DeleteEndpointNonexistentIdempotent.cs` |
| `sns.delete-platform-application-nonexistent-idempotent` | `4df2823` | ported | `Sns/DeletePlatformApplicationNonexistentIdempotent.cs` |
| `sns.delete-topic-cascades-subscriptions` | `4df2823` | ported | `Sns/DeleteTopicCascadesSubscriptions.cs` |
| `sns.delete-topic-then-recreate-starts-fresh` | `4df2823` | ported | `Sns/DeleteTopicThenRecreateStartsFresh.cs` |
| `sns.deleted-endpoint-queue-dead-letters-each-subscription-in-its-own-format` | `4df2823` | ported | `Sns/DeletedEndpointQueueDeadLettersEachSubscriptionInItsOwnFormat.cs` |
| `sns.deploy-cfn-fifo-topic-delivers-in-order-and-deduplicates-by-content` | `4df2823` | not ported | CloudFormation stacks |
| `sns.deploy-cfn-kms-encrypted-topic-and-queue-deliver-plaintext` | `4df2823` | not ported | CloudFormation stacks |
| `sns.deploy-cfn-removing-a-topic-inline-policy-restores-the-default-policy` | `4df2823` | not ported | CloudFormation stacks |
| `sns.deploy-cfn-stack-update-changes-a-filter-policy-in-place` | `4df2823` | not ported | CloudFormation stacks |
| `sns.deploy-cfn-subscription-filter-policy-behaves` | `4df2823` | not ported | CloudFormation stacks |
| `sns.deploy-cfn-subscription-redrive-policy-dead-letters-an-undeliverable-message` | `4df2823` | not ported | CloudFormation stacks |
| `sns.deploy-cfn-topic-policy-authorizes-only-the-named-role-and-follows-updates` | `4df2823` | not ported | CloudFormation stacks |
| `sns.deploy-cfn-topic-publish-subscribe-flow` | `4df2823` | not ported | CloudFormation stacks |
| `sns.display-name-over-100-characters-rejected` | `4df2823` | ported | `Sns/DisplayNameOver100CharactersRejected.cs` |
| `sns.e2e-topic-fanout-filter-teardown-flow` | `4df2823` | ported | `Sns/E2eTopicFanoutFilterTeardownFlow.cs` |
| `sns.email-subscription-confirmation-state-attributes` | `4df2823` | ported | `Sns/EmailSubscriptionConfirmationStateAttributes.cs` |
| `sns.empty-vs-absent-message-attribute-rejected` | `4df2823` | ported | `Sns/EmptyVsAbsentMessageAttributeRejected.cs` |
| `sns.encrypted-topic-whose-key-policy-withholds-usage-refuses-publish` | `4df2823` | not ported | KMS keys |
| `sns.feedback-role-without-logs-permission-still-delivers-and-writes-no-log-record` | `4df2823` | not ported | CloudWatch Logs delivery-status logging (IAM, Logs) |
| `sns.fifo-archive-replay-redelivers-archived-messages-to-a-new-subscription` | `4df2823` | ported | `Sns/FifoArchiveReplayRedeliversArchivedMessagesToANewSubscription.cs` |
| `sns.fifo-concurrent-duplicate-publish-dedup` | `4df2823` | ported | `Sns/FifoConcurrentDuplicatePublishDedup.cs` |
| `sns.fifo-concurrent-publishers-across-many-groups-keep-each-group-in-order` | `4df2823` | ported | `Sns/FifoConcurrentPublishersAcrossManyGroupsKeepEachGroupInOrder.cs` |
| `sns.fifo-deduplication-collapses-duplicate-publish` | `4df2823` | ported | `Sns/FifoDeduplicationCollapsesDuplicatePublish.cs` |
| `sns.fifo-delivery-preserves-order-per-group-and-carries-the-group-id` | `4df2823` | ported | `Sns/FifoDeliveryPreservesOrderPerGroupAndCarriesTheGroupId.cs` |
| `sns.fifo-message-group-and-deduplication-ids-over-128-characters-rejected` | `4df2823` | ported | `Sns/FifoMessageGroupAndDeduplicationIdsOver128CharactersRejected.cs` |
| `sns.fifo-message-group-dedup-scope-holds-per-group-under-concurrent-publishers` | `4df2823` | ported | `Sns/FifoMessageGroupDedupScopeHoldsPerGroupUnderConcurrentPublishers.cs` |
| `sns.fifo-message-group-id-validation` | `4df2823` | ported | `Sns/FifoMessageGroupIdValidation.cs` |
| `sns.fifo-publish-without-a-message-group-id-rejected-a-grouped-publish-delivers` | `4df2823` | ported | `Sns/FifoPublishWithoutAMessageGroupIdRejectedAGroupedPublishDelivers.cs` |
| `sns.fifo-sequence-numbers-monotonic-within-group` | `4df2823` | ported | `Sns/FifoSequenceNumbersMonotonicWithinGroup.cs` |
| `sns.fifo-subscription-redrive-requires-a-fifo-dlq-and-dead-letters-into-it` | `4df2823` | ported | `Sns/FifoSubscriptionRedriveRequiresAFifoDlqAndDeadLettersIntoIt.cs` |
| `sns.fifo-throughput-scope-message-group-narrows-the-deduplication-scope` | `4df2823` | ported | `Sns/FifoThroughputScopeMessageGroupNarrowsTheDeduplicationScope.cs` |
| `sns.fifo-throughput-scope-on-a-standard-topic-or-an-unknown-value-rejected` | `4df2823` | ported | `Sns/FifoThroughputScopeOnAStandardTopicOrAnUnknownValueRejected.cs` |
| `sns.fifo-topic-accepts-both-queue-types` | `4df2823` | ported | `Sns/FifoTopicAcceptsBothQueueTypes.cs` |
| `sns.fifo-topic-attributes-roundtrip` | `4df2823` | ported | `Sns/FifoTopicAttributesRoundtrip.cs` |
| `sns.fifo-topic-fans-filtered-messages-to-fifo-queues-in-group-order-and-the-deleted-topic-refuses-publish` | `4df2823` | ported | `Sns/FifoTopicFansFilteredMessagesToFifoQueuesInGroupOrderAndTheDeletedTopicRefusesPublish.cs` |
| `sns.fifo-topic-name-suffix-required` | `4df2823` | ported | `Sns/FifoTopicNameSuffixRequired.cs` |
| `sns.fifo-topic-ordering-delivery` | `4df2823` | ported | `Sns/FifoTopicOrderingDelivery.cs` |
| `sns.fifo-topic-preserves-publish-order-and-deduplicates-by-id` | `4df2823` | ported | `Sns/FifoTopicPreservesPublishOrderAndDeduplicatesById.cs` |
| `sns.fifo-topic-rejects-email-protocol` | `4df2823` | ported | `Sns/FifoTopicRejectsEmailProtocol.cs` |
| `sns.filter-policy-cidr-matches-ip-address-attributes` | `4df2823` | ported | `Sns/FilterPolicyCidrMatchesIpAddressAttributes.cs` |
| `sns.filter-policy-exists-false-and-anything-but-decide-delivery` | `4df2823` | ported | `Sns/FilterPolicyExistsFalseAndAnythingButDecideDelivery.cs` |
| `sns.filter-policy-message-body-scope-cidr-and-or-conjunction-decide-delivery` | `4df2823` | ported | `Sns/FilterPolicyMessageBodyScopeCidrAndOrConjunctionDecideDelivery.cs` |
| `sns.filter-policy-message-body-scope-counts-nested-leaf-keys-toward-the-key-limit` | `4df2823` | ported | `Sns/FilterPolicyMessageBodyScopeCountsNestedLeafKeysTowardTheKeyLimit.cs` |
| `sns.filter-policy-message-body-scope-matches-array-members-and-withholds-non-json` | `4df2823` | ported | `Sns/FilterPolicyMessageBodyScopeMatchesArrayMembersAndWithholdsNonJson.cs` |
| `sns.filter-policy-message-body-scope-numeric-exists-and-anything-but-decide-delivery` | `4df2823` | ported | `Sns/FilterPolicyMessageBodyScopeNumericExistsAndAnythingButDecideDelivery.cs` |
| `sns.filter-policy-numeric-operator-boundary-decides-delivery` | `4df2823` | ported | `Sns/FilterPolicyNumericOperatorBoundaryDecidesDelivery.cs` |
| `sns.filter-policy-numeric-operator-ignores-a-string-typed-attribute` | `4df2823` | ported | `Sns/FilterPolicyNumericOperatorIgnoresAStringTypedAttribute.cs` |
| `sns.filter-policy-operator-vocabulary-round-trip` | `4df2823` | ported | `Sns/FilterPolicyOperatorVocabularyRoundTrip.cs` |
| `sns.filter-policy-or-and-cross-key-conjunction-decide-delivery` | `4df2823` | ported | `Sns/FilterPolicyOrAndCrossKeyConjunctionDecideDelivery.cs` |
| `sns.filter-policy-over-150-value-combinations-rejected` | `4df2823` | ported | `Sns/FilterPolicyOver150ValueCombinationsRejected.cs` |
| `sns.filter-policy-scope-constrains-policy-shape` | `4df2823` | ported | `Sns/FilterPolicyScopeConstrainsPolicyShape.cs` |
| `sns.filter-policy-scope-set-without-a-policy-is-hidden-until-a-policy-is-set` | `4df2823` | ported | `Sns/FilterPolicyScopeSetWithoutAPolicyIsHiddenUntilAPolicyIsSet.cs` |
| `sns.filter-policy-scope-switch-away-from-message-body-rejected-while-policy-is-nested` | `4df2823` | ported | `Sns/FilterPolicyScopeSwitchAwayFromMessageBodyRejectedWhilePolicyIsNested.cs` |
| `sns.filter-policy-scope-switch-on-a-live-subscription-reroutes-delivery` | `4df2823` | ported | `Sns/FilterPolicyScopeSwitchOnALiveSubscriptionReroutesDelivery.cs` |
| `sns.filter-policy-scopes-delivery-and-lifting-it-restores-delivery` | `4df2823` | ported | `Sns/FilterPolicyScopesDeliveryAndLiftingItRestoresDelivery.cs` |
| `sns.filter-policy-string-operators-decide-delivery` | `4df2823` | ported | `Sns/FilterPolicyStringOperatorsDecideDelivery.cs` |
| `sns.filter-policy-unknown-operator-rejected` | `4df2823` | ported | `Sns/FilterPolicyUnknownOperatorRejected.cs` |
| `sns.filtered-out-message-is-not-dead-lettered-for-a-deleted-endpoint` | `4df2823` | ported | `Sns/FilteredOutMessageIsNotDeadLetteredForADeletedEndpoint.cs` |
| `sns.firehose-raw-subscription-writes-the-bare-message-bodies` | `4df2823` | not ported | Firehose subscriptions (Firehose, S3, IAM) |
| `sns.firehose-subscribe-without-a-subscription-role-rejected` | `4df2823` | not ported | Firehose subscriptions (Firehose, S3, IAM) |
| `sns.firehose-subscription-role-without-put-record-dead-letters-until-granted` | `4df2823` | not ported | Firehose subscriptions (Firehose, S3, IAM) |
| `sns.firehose-subscription-writes-each-notification-envelope-into-the-stream-bucket` | `4df2823` | not ported | Firehose subscriptions (Firehose, S3, IAM) |
| `sns.get-endpoint-attributes-not-found` | `4df2823` | ported | `Sns/GetEndpointAttributesNotFound.cs` |
| `sns.get-sms-attributes-unknown-name-rejected` | `4df2823` | ported | `Sns/GetSmsAttributesUnknownNameRejected.cs` |
| `sns.get-topic-attributes-malformed-arn-rejected` | `4df2823` | ported | `Sns/GetTopicAttributesMalformedArnRejected.cs` |
| `sns.https-endpoint-failing-with-500-is-retried-then-dead-lettered` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.https-raw-message-delivery-posts-the-bare-body-with-the-rawdelivery-header` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.https-subscription-receives-nothing-until-confirmed-then-gets-the-notification` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.https-subscription-with-a-zero-retry-delivery-policy-attempts-a-failing-notification-exactly-once` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.lambda-feedback-roles-log-the-invocation-delivery-record-and-the-uninvokable-function-failure` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.lambda-function-error-is-retried-by-lambda-and-never-dead-lettered-by-sns` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.lambda-subscription-invocation-carries-the-sns-records-envelope` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.lambda-subscription-the-function-refuses-to-sns-dead-letters-into-the-subscription-dlq` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.list-endpoints-by-nonexistent-application-fails` | `4df2823` | ported | `Sns/ListEndpointsByNonexistentApplicationFails.cs` |
| `sns.list-platform-applications-invalid-next-token-rejected` | `4df2823` | ported | `Sns/ListPlatformApplicationsInvalidNextTokenRejected.cs` |
| `sns.list-sms-sandbox-phone-numbers-max-results-over-limit-rejected` | `4df2823` | ported | `Sns/ListSmsSandboxPhoneNumbersMaxResultsOverLimitRejected.cs` |
| `sns.list-subscriptions-by-topic-pagination-exactness` | `4df2823` | ported | `Sns/ListSubscriptionsByTopicPaginationExactness.cs` |
| `sns.list-subscriptions-by-topic-reflects-state` | `4df2823` | ported | `Sns/ListSubscriptionsByTopicReflectsState.cs` |
| `sns.list-subscriptions-pages-every-subscription-exactly-once` | `4df2823` | ported | `Sns/ListSubscriptionsPagesEverySubscriptionExactlyOnce.cs` |
| `sns.list-subscriptions-paginates-and-drops-unsubscribed` | `4df2823` | ported | `Sns/ListSubscriptionsPaginatesAndDropsUnsubscribed.cs` |
| `sns.list-tags-for-nonexistent-topic-fails` | `4df2823` | ported | `Sns/ListTagsForNonexistentTopicFails.cs` |
| `sns.list-topics-and-subscriptions-forged-next-token-rejected` | `4df2823` | ported | `Sns/ListTopicsAndSubscriptionsForgedNextTokenRejected.cs` |
| `sns.list-topics-paginates-every-created-topic-exactly-once` | `4df2823` | ported | `Sns/ListTopicsPaginatesEveryCreatedTopicExactlyOnce.cs` |
| `sns.message-attribute-name-length-and-period-rules-rejected` | `4df2823` | ported | `Sns/MessageAttributeNameLengthAndPeriodRulesRejected.cs` |
| `sns.message-attribute-value-in-the-wrong-field-for-its-data-type-rejected` | `4df2823` | ported | `Sns/MessageAttributeValueInTheWrongFieldForItsDataTypeRejected.cs` |
| `sns.message-attributes-survive-delivery-byte-for-byte` | `4df2823` | ported | `Sns/MessageAttributesSurviveDeliveryByteForByte.cs` |
| `sns.message-structure-json-selects-the-protocol-body-and-falls-back-to-default` | `4df2823` | ported | `Sns/MessageStructureJsonSelectsTheProtocolBodyAndFallsBackToDefault.cs` |
| `sns.message-structure-json-stringifies-scalar-branches-and-ignores-object-branches` | `4df2823` | ported | `Sns/MessageStructureJsonStringifiesScalarBranchesAndIgnoresObjectBranches.cs` |
| `sns.notification-signature-verifies-against-the-signing-certificate` | `4df2823` | ported | `Sns/NotificationSignatureVerifiesAgainstTheSigningCertificate.cs` |
| `sns.number-attribute-beyond-the-precision-or-scale-bounds-rejected` | `4df2823` | ported | `Sns/NumberAttributeBeyondThePrecisionOrScaleBoundsRejected.cs` |
| `sns.op-confirm-subscription-invalid-token-rejected` | `4df2823` | ported | `Sns/OpConfirmSubscriptionInvalidTokenRejected.cs` |
| `sns.op-create-topic-invalid-name-rejected` | `4df2823` | ported | `Sns/OpCreateTopicInvalidNameRejected.cs` |
| `sns.op-set-subscription-attributes-filter-policy-roundtrip` | `4df2823` | ported | `Sns/OpSetSubscriptionAttributesFilterPolicyRoundtrip.cs` |
| `sns.op-set-subscription-attributes-invalid-name-rejected` | `4df2823` | ported | `Sns/OpSetSubscriptionAttributesInvalidNameRejected.cs` |
| `sns.op-topic-attribute-defaults-and-effective-delivery-policy` | `4df2823` | ported | `Sns/OpTopicAttributeDefaultsAndEffectiveDeliveryPolicy.cs` |
| `sns.opt-in-phone-number-invalid-rejected` | `4df2823` | ported | `Sns/OptInPhoneNumberInvalidRejected.cs` |
| `sns.overlapping-filters-fan-one-message-to-every-matching-queue-and-the-deleted-topic-refuses-publish-and-listing` | `4df2823` | ported | `Sns/OverlappingFiltersFanOneMessageToEveryMatchingQueueAndTheDeletedTopicRefusesPublishAndListing.cs` |
| `sns.pending-subscription-arn-placeholder-not-resolvable` | `4df2823` | ported | `Sns/PendingSubscriptionArnPlaceholderNotResolvable.cs` |
| `sns.phone-number-opt-out-state` | `4df2823` | ported | `Sns/PhoneNumberOptOutState.cs` |
| `sns.platform-application-bogus-credential-rejected` | `4df2823` | known gap: Other | `Sns/PlatformApplicationBogusCredentialRejected.cs` |
| `sns.platform-application-custom-attribute-round-trip` | `4df2823` | ported | `Sns/PlatformApplicationCustomAttributeRoundTrip.cs` |
| `sns.platform-application-not-found` | `4df2823` | ported | `Sns/PlatformApplicationNotFound.cs` |
| `sns.publish-batch-aggregate-size-at-the-exact-262144-byte-boundary` | `4df2823` | ported | `Sns/PublishBatchAggregateSizeAtTheExact262144ByteBoundary.cs` |
| `sns.publish-batch-content-based-dedup-collapses-duplicate-bodies-in-one-call` | `4df2823` | ported | `Sns/PublishBatchContentBasedDedupCollapsesDuplicateBodiesInOneCall.cs` |
| `sns.publish-batch-duplicate-ids-rejected` | `4df2823` | ported | `Sns/PublishBatchDuplicateIdsRejected.cs` |
| `sns.publish-batch-empty-fails` | `4df2823` | ported | `Sns/PublishBatchEmptyFails.cs` |
| `sns.publish-batch-entry-group-or-dedup-id-over-128-characters-rejects-the-whole-batch` | `4df2823` | ported | `Sns/PublishBatchEntryGroupOrDedupIdOver128CharactersRejectsTheWholeBatch.cs` |
| `sns.publish-batch-entry-id-over-80-characters-or-with-invalid-characters-rejected` | `4df2823` | ported | `Sns/PublishBatchEntryIdOver80CharactersOrWithInvalidCharactersRejected.cs` |
| `sns.publish-batch-fifo-orders-entries-and-dedups-across-batches` | `4df2823` | ported | `Sns/PublishBatchFifoOrdersEntriesAndDedupsAcrossBatches.cs` |
| `sns.publish-batch-filter-policy-is-evaluated-per-entry` | `4df2823` | ported | `Sns/PublishBatchFilterPolicyIsEvaluatedPerEntry.cs` |
| `sns.publish-batch-one-call-two-message-groups-each-keeps-its-own-order` | `4df2823` | ported | `Sns/PublishBatchOneCallTwoMessageGroupsEachKeepsItsOwnOrder.cs` |
| `sns.publish-batch-one-invalid-fifo-entry-rejected-and-no-sibling-delivered` | `4df2823` | ported | `Sns/PublishBatchOneInvalidFifoEntryRejectedAndNoSiblingDelivered.cs` |
| `sns.publish-batch-success-delivery` | `4df2823` | ported | `Sns/PublishBatchSuccessDelivery.cs` |
| `sns.publish-batch-to-a-deleted-topic-not-found` | `4df2823` | ported | `Sns/PublishBatchToADeletedTopicNotFound.cs` |
| `sns.publish-batch-too-long-in-aggregate-rejected-and-nothing-delivered` | `4df2823` | ported | `Sns/PublishBatchTooLongInAggregateRejectedAndNothingDelivered.cs` |
| `sns.publish-batch-too-many-entries-rejected` | `4df2823` | ported | `Sns/PublishBatchTooManyEntriesRejected.cs` |
| `sns.publish-batch-validation-is-request-level` | `4df2823` | ported | `Sns/PublishBatchValidationIsRequestLevel.cs` |
| `sns.publish-binary-message-attribute-byte-preserved` | `4df2823` | ported | `Sns/PublishBinaryMessageAttributeBytePreserved.cs` |
| `sns.publish-by-target-arn-naming-a-topic-delivers-like-topic-arn` | `4df2823` | ported | `Sns/PublishByTargetArnNamingATopicDeliversLikeTopicArn.cs` |
| `sns.publish-delivers-json-envelope-to-sqs` | `4df2823` | ported | `Sns/PublishDeliversJsonEnvelopeToSqs.cs` |
| `sns.publish-empty-message-fails` | `4df2823` | ported | `Sns/PublishEmptyMessageFails.cs` |
| `sns.publish-message-size-limit-enforced` | `4df2823` | ported | `Sns/PublishMessageSizeLimitEnforced.cs` |
| `sns.publish-message-structure-json-semantics` | `4df2823` | ported | `Sns/PublishMessageStructureJsonSemantics.cs` |
| `sns.publish-non-bmp-message-attribute-value-rejected` | `4df2823` | ported | `Sns/PublishNonBmpMessageAttributeValueRejected.cs` |
| `sns.publish-of-exactly-256-kib-delivered-intact-and-one-byte-more-rejected` | `4df2823` | ported | `Sns/PublishOfExactly256KibDeliveredIntactAndOneByteMoreRejected.cs` |
| `sns.publish-size-counts-attribute-name-type-and-value-but-not-the-subject` | `4df2823` | ported | `Sns/PublishSizeCountsAttributeNameTypeAndValueButNotTheSubject.cs` |
| `sns.publish-size-limit-counts-message-attributes` | `4df2823` | ported | `Sns/PublishSizeLimitCountsMessageAttributes.cs` |
| `sns.publish-still-delivers-to-siblings-when-a-subscribed-queue-is-deleted` | `4df2823` | ported | `Sns/PublishStillDeliversToSiblingsWhenASubscribedQueueIsDeleted.cs` |
| `sns.publish-to-deleted-topic-not-found` | `4df2823` | ported | `Sns/PublishToDeletedTopicNotFound.cs` |
| `sns.publish-to-topic-whose-key-is-pending-deletion-fails-kms-invalid-state` | `4df2823` | not ported | KMS keys |
| `sns.publish-unicode-body-preserved` | `4df2823` | ported | `Sns/PublishUnicodeBodyPreserved.cs` |
| `sns.publish-with-a-reserved-attribute-name-prefix-rejected` | `4df2823` | ported | `Sns/PublishWithAReservedAttributeNamePrefixRejected.cs` |
| `sns.publish-without-target-fails` | `4df2823` | ported | `Sns/PublishWithoutTargetFails.cs` |
| `sns.queue-policy-source-account-condition-decides-delivery` | `4df2823` | ported | `Sns/QueuePolicySourceAccountConditionDecidesDelivery.cs` |
| `sns.queue-policy-source-arn-of-another-topic-withholds-delivery` | `4df2823` | ported | `Sns/QueuePolicySourceArnOfAnotherTopicWithholdsDelivery.cs` |
| `sns.raw-delivery-normalises-number-attributes-the-envelope-carries-verbatim` | `4df2823` | ported | `Sns/RawDeliveryNormalisesNumberAttributesTheEnvelopeCarriesVerbatim.cs` |
| `sns.raw-delivery-to-sqs-drops-a-message-with-more-than-ten-attributes` | `4df2823` | ported | `Sns/RawDeliveryToSqsDropsAMessageWithMoreThanTenAttributes.cs` |
| `sns.raw-delivery-toggle-on-a-live-subscription-switches-the-delivered-format` | `4df2823` | ported | `Sns/RawDeliveryToggleOnALiveSubscriptionSwitchesTheDeliveredFormat.cs` |
| `sns.raw-message-delivery-drops-message-attributes-for-an-https-subscriber-but-keeps-them-for-sqs` | `4df2823` | not ported | HTTP/S or Lambda endpoints, which these checks build from Lambda functions and IAM roles |
| `sns.raw-message-delivery-flag-is-case-insensitive-and-reads-back-lowercase` | `4df2823` | ported | `Sns/RawMessageDeliveryFlagIsCaseInsensitiveAndReadsBackLowercase.cs` |
| `sns.raw-message-delivery-strips-the-envelope-for-only-that-subscription` | `4df2823` | ported | `Sns/RawMessageDeliveryStripsTheEnvelopeForOnlyThatSubscription.cs` |
| `sns.recreated-queue-with-the-same-name-resumes-delivery-on-the-old-subscription` | `4df2823` | ported | `Sns/RecreatedQueueWithTheSameNameResumesDeliveryOnTheOldSubscription.cs` |
| `sns.remove-permission-unknown-label-is-idempotent` | `4df2823` | ported | `Sns/RemovePermissionUnknownLabelIsIdempotent.cs` |
| `sns.replay-policy-on-a-topic-without-an-archive-policy-rejected` | `4df2823` | ported | `Sns/ReplayPolicyOnATopicWithoutAnArchivePolicyRejected.cs` |
| `sns.resubscribe-after-unsubscribe-mints-a-new-arn` | `4df2823` | ported | `Sns/ResubscribeAfterUnsubscribeMintsANewArn.cs` |
| `sns.same-topic-name-in-two-regions-is-two-independent-topics` | `4df2823` | ported | `Sns/SameTopicNameInTwoRegionsIsTwoIndependentTopics.cs` |
| `sns.set-endpoint-attributes-not-found` | `4df2823` | ported | `Sns/SetEndpointAttributesNotFound.cs` |
| `sns.set-platform-application-attributes-unknown-key-rejected` | `4df2823` | ported | `Sns/SetPlatformApplicationAttributesUnknownKeyRejected.cs` |
| `sns.set-sms-attributes-invalid-name-rejected` | `4df2823` | ported | `Sns/SetSmsAttributesInvalidNameRejected.cs` |
| `sns.set-sms-attributes-invalid-sms-type-rejected` | `4df2823` | ported | `Sns/SetSmsAttributesInvalidSmsTypeRejected.cs` |
| `sns.set-subscription-attributes-nonexistent-fails` | `4df2823` | ported | `Sns/SetSubscriptionAttributesNonexistentFails.cs` |
| `sns.set-topic-attribute-invalid-policy-rejected` | `4df2823` | ported | `Sns/SetTopicAttributeInvalidPolicyRejected.cs` |
| `sns.set-topic-attributes-cross-account-feedback-role-rejected` | `4df2823` | not ported | IAM roles and role assumption |
| `sns.set-topic-attributes-invalid-signature-version-rejected` | `4df2823` | ported | `Sns/SetTopicAttributesInvalidSignatureVersionRejected.cs` |
| `sns.set-topic-attributes-invalid-tracing-config-rejected` | `4df2823` | ported | `Sns/SetTopicAttributesInvalidTracingConfigRejected.cs` |
| `sns.set-topic-attributes-malformed-feedback-role-arn-rejected` | `4df2823` | not ported | IAM roles and role assumption |
| `sns.set-topic-attributes-unknown-name-fails` | `4df2823` | ported | `Sns/SetTopicAttributesUnknownNameFails.cs` |
| `sns.signature-version-two-switches-the-delivered-digest-to-sha256` | `4df2823` | ported | `Sns/SignatureVersionTwoSwitchesTheDeliveredDigestToSha256.cs` |
| `sns.sms-account-state-reads` | `4df2823` | ported | `Sns/SmsAccountStateReads.cs` |
| `sns.sms-attributes-custom-value-round-trip` | `4df2823` | ported | `Sns/SmsAttributesCustomValueRoundTrip.cs` |
| `sns.sms-attributes-filter-exactness-and-clear-via-empty` | `4df2823` | ported | `Sns/SmsAttributesFilterExactnessAndClearViaEmpty.cs` |
| `sns.sms-sandbox-delete-unknown-number` | `4df2823` | ported | `Sns/SmsSandboxDeleteUnknownNumber.cs` |
| `sns.sqs-failure-feedback-role-logs-the-denied-delivery-while-the-message-never-reaches-the-queue` | `4df2823` | not ported | CloudWatch Logs delivery-status logging (IAM, Logs) |
| `sns.sqs-subscription-delivers-the-published-message-unconditionally` | `4df2823` | ported | `Sns/SqsSubscriptionDeliversThePublishedMessageUnconditionally.cs` |
| `sns.sqs-success-feedback-role-writes-the-delivery-record-to-cloudwatch-logs` | `4df2823` | not ported | CloudWatch Logs delivery-status logging (IAM, Logs) |
| `sns.standard-topic-message-group-id-reaches-the-queue-as-its-message-group-id` | `4df2823` | ported | `Sns/StandardTopicMessageGroupIdReachesTheQueueAsItsMessageGroupId.cs` |
| `sns.standard-topic-rejects-fifo-dedup-id` | `4df2823` | ported | `Sns/StandardTopicRejectsFifoDedupId.cs` |
| `sns.string-array-message-attribute-matches-a-member-and-binary-is-ignored` | `4df2823` | ported | `Sns/StringArrayMessageAttributeMatchesAMemberAndBinaryIsIgnored.cs` |
| `sns.subject-over-100-characters-rejected-and-the-limit-counts-characters` | `4df2823` | ported | `Sns/SubjectOver100CharactersRejectedAndTheLimitCountsCharacters.cs` |
| `sns.subject-with-a-line-break-or-control-character-rejected` | `4df2823` | ported | `Sns/SubjectWithALineBreakOrControlCharacterRejected.cs` |
| `sns.subscribe-email-pending-confirmation` | `4df2823` | ported | `Sns/SubscribeEmailPendingConfirmation.cs` |
| `sns.subscribe-fifo-queue-to-standard-topic-rejected` | `4df2823` | ported | `Sns/SubscribeFifoQueueToStandardTopicRejected.cs` |
| `sns.subscribe-invalid-endpoint-rejected` | `4df2823` | ported | `Sns/SubscribeInvalidEndpointRejected.cs` |
| `sns.subscribe-invalid-filter-policy-fails` | `4df2823` | ported | `Sns/SubscribeInvalidFilterPolicyFails.cs` |
| `sns.subscribe-raw-message-delivery-end-to-end` | `4df2823` | ported | `Sns/SubscribeRawMessageDeliveryEndToEnd.cs` |
| `sns.subscribe-same-endpoint-is-idempotent` | `4df2823` | ported | `Sns/SubscribeSameEndpointIsIdempotent.cs` |
| `sns.subscribe-sqs-auto-confirms` | `4df2823` | ported | `Sns/SubscribeSqsAutoConfirms.cs` |
| `sns.subscribe-to-nonexistent-topic-fails` | `4df2823` | ported | `Sns/SubscribeToNonexistentTopicFails.cs` |
| `sns.subscribe-unknown-protocol-rejected` | `4df2823` | ported | `Sns/SubscribeUnknownProtocolRejected.cs` |
| `sns.subscribe-with-an-unknown-attribute-rejected-and-creates-nothing` | `4df2823` | ported | `Sns/SubscribeWithAnUnknownAttributeRejectedAndCreatesNothing.cs` |
| `sns.subscription-created-with-attributes-roundtrip` | `4df2823` | ported | `Sns/SubscriptionCreatedWithAttributesRoundtrip.cs` |
| `sns.subscription-delivery-policy-reads-back-normalized-on-an-sqs-subscription` | `4df2823` | ported | `Sns/SubscriptionDeliveryPolicyReadsBackNormalizedOnAnSqsSubscription.cs` |
| `sns.subscription-filter-policy-delivery` | `4df2823` | ported | `Sns/SubscriptionFilterPolicyDelivery.cs` |
| `sns.subscription-filter-policy-update-and-validation` | `4df2823` | ported | `Sns/SubscriptionFilterPolicyUpdateAndValidation.cs` |
| `sns.subscription-raw-message-delivery-attribute` | `4df2823` | ported | `Sns/SubscriptionRawMessageDeliveryAttribute.cs` |
| `sns.subscription-redrive-policy-non-sqs-target-rejected` | `4df2823` | ported | `Sns/SubscriptionRedrivePolicyNonSqsTargetRejected.cs` |
| `sns.subscription-redrive-policy-round-trip-and-replace` | `4df2823` | ported | `Sns/SubscriptionRedrivePolicyRoundTripAndReplace.cs` |
| `sns.success-feedback-sample-rate-zero-writes-no-log-while-full-rate-logs-every-delivery` | `4df2823` | not ported | CloudWatch Logs delivery-status logging (IAM, Logs) |
| `sns.tag-key-over-128-characters-rejected` | `4df2823` | ported | `Sns/TagKeyOver128CharactersRejected.cs` |
| `sns.tag-resource-lifecycle` | `4df2823` | ported | `Sns/TagResourceLifecycle.cs` |
| `sns.tag-resource-lifecycle-and-limit` | `4df2823` | ported | `Sns/TagResourceLifecycleAndLimit.cs` |
| `sns.tag-resource-nonexistent-topic-fails` | `4df2823` | ported | `Sns/TagResourceNonexistentTopicFails.cs` |
| `sns.tag-resource-reserved-aws-prefix-rejected` | `4df2823` | ported | `Sns/TagResourceReservedAwsPrefixRejected.cs` |
| `sns.topic-arn-of-another-region-rejected-by-a-regional-endpoint` | `4df2823` | ported | `Sns/TopicArnOfAnotherRegionRejectedByARegionalEndpoint.cs` |
| `sns.topic-attributes-subscription-counters-track-state` | `4df2823` | ported | `Sns/TopicAttributesSubscriptionCountersTrackState.cs` |
| `sns.topic-create-idempotent-by-name` | `4df2823` | ported | `Sns/TopicCreateIdempotentByName.cs` |
| `sns.topic-delete-lifecycle-and-idempotent-delete` | `4df2823` | ported | `Sns/TopicDeleteLifecycleAndIdempotentDelete.cs` |
| `sns.topic-delivery-policy-computes-effective-policy` | `4df2823` | ported | `Sns/TopicDeliveryPolicyComputesEffectivePolicy.cs` |
| `sns.topic-feedback-role-arn-and-sample-rate-round-trip` | `4df2823` | not ported | IAM roles and role assumption |
| `sns.topic-feedback-sample-rate-out-of-range-rejected` | `4df2823` | ported | `Sns/TopicFeedbackSampleRateOutOfRangeRejected.cs` |
| `sns.topic-maximum-message-size-above-256kib-requires-eligible-subscriptions` | `4df2823` | ported | `Sns/TopicMaximumMessageSizeAbove256kibRequiresEligibleSubscriptions.cs` |
| `sns.topic-maximum-message-size-round-trip-and-bounds-rejected` | `4df2823` | ported | `Sns/TopicMaximumMessageSizeRoundTripAndBoundsRejected.cs` |
| `sns.topic-mutable-attributes-round-trip-and-clear` | `4df2823` | ported | `Sns/TopicMutableAttributesRoundTripAndClear.cs` |
| `sns.topic-name-ending-in-fifo-without-the-fifo-topic-attribute-rejected` | `4df2823` | ported | `Sns/TopicNameEndingInFifoWithoutTheFifoTopicAttributeRejected.cs` |
| `sns.topic-policy-add-remove-permission` | `4df2823` | ported | `Sns/TopicPolicyAddRemovePermission.cs` |
| `sns.topic-policy-endpoint-condition-limits-which-queue-a-role-may-subscribe` | `4df2823` | not ported | IAM roles and role assumption |
| `sns.topic-policy-principal-arn-pattern-admits-only-matching-roles` | `4df2823` | not ported | IAM roles and role assumption |
| `sns.topic-policy-protocol-condition-blocks-a-real-signed-subscribe` | `4df2823` | ported | `Sns/TopicPolicyProtocolConditionBlocksARealSignedSubscribe.cs` |
| `sns.topic-policy-round-trips-and-deleted-topic-set-attributes-not-found` | `4df2823` | ported | `Sns/TopicPolicyRoundTripsAndDeletedTopicSetAttributesNotFound.cs` |
| `sns.topic-policy-subscribe-only-grant-refuses-the-roles-publish` | `4df2823` | not ported | IAM roles and role assumption |
| `sns.topic-set-attribute-immutable-rejected` | `4df2823` | ported | `Sns/TopicSetAttributeImmutableRejected.cs` |
| `sns.topic-subscription-counters-converge-after-concurrent-subscribes-and-unsubscribes` | `4df2823` | ported | `Sns/TopicSubscriptionCountersConvergeAfterConcurrentSubscribesAndUnsubscribes.cs` |
| `sns.topic-tags-at-create-overwrite-and-untag-absent-key` | `4df2823` | ported | `Sns/TopicTagsAtCreateOverwriteAndUntagAbsentKey.cs` |
| `sns.unsubscribe-malformed-arn-fails` | `4df2823` | ported | `Sns/UnsubscribeMalformedArnFails.cs` |
| `sns.unsubscribe-nonexistent-is-idempotent` | `4df2823` | ported | `Sns/UnsubscribeNonexistentIsIdempotent.cs` |
| `sns.unsubscribe-pending-subscription-rejected` | `4df2823` | ported | `Sns/UnsubscribePendingSubscriptionRejected.cs` |
| `sns.unsubscribe-removes-subscription` | `4df2823` | ported | `Sns/UnsubscribeRemovesSubscription.cs` |
| `sns.unsubscribe-stops-delivery` | `4df2823` | ported | `Sns/UnsubscribeStopsDelivery.cs` |
| `sns.unsubscribe-stops-delivery-and-attribute-lookup-is-rejected` | `4df2823` | ported | `Sns/UnsubscribeStopsDeliveryAndAttributeLookupIsRejected.cs` |
| `sns.verify-sms-sandbox-wrong-otp-rejected` | `4df2823` | ported | `Sns/VerifySmsSandboxWrongOtpRejected.cs` |

## behavioral/sns__sqs

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `sns__sqs.a-message-filtered-out-at-publish-time-is-never-redelivered-when-the-policy-is-relaxed` | `4df2823` | ported | `SnsSqs/AMessageFilteredOutAtPublishTimeIsNeverRedeliveredWhenThePolicyIsRelaxed.cs` |
| `sns__sqs.fifo-topic-subscription-filter-policy-withholds-non-matching-messages` | `4df2823` | ported | `SnsSqs/FifoTopicSubscriptionFilterPolicyWithholdsNonMatchingMessages.cs` |
| `sns__sqs.message-attribute-name-outside-the-allowed-charset-rejected` | `4df2823` | ported | `SnsSqs/MessageAttributeNameOutsideTheAllowedCharsetRejected.cs` |
| `sns__sqs.platform-application-event-endpoint-created-notifies-configured-topic` | `4df2823` | ported | `SnsSqs/PlatformApplicationEventEndpointCreatedNotifiesConfiguredTopic.cs` |
| `sns__sqs.queue-maximum-message-size-does-not-bound-sns-delivery` | `4df2823` | ported | `SnsSqs/QueueMaximumMessageSizeDoesNotBoundSnsDelivery.cs` |
| `sns__sqs.sns-filter-policy-message-body-scope-suppresses-non-matching` | `4df2823` | ported | `SnsSqs/SnsFilterPolicyMessageBodyScopeSuppressesNonMatching.cs` |
| `sns__sqs.sns-filter-policy-scope-is-orthogonal-and-message-body-matches-nested-keys` | `4df2823` | ported | `SnsSqs/SnsFilterPolicyScopeIsOrthogonalAndMessageBodyMatchesNestedKeys.cs` |
| `sns__sqs.sns-publish-batch-delivers-one-envelope-per-entry-to-the-subscribed-queue` | `4df2823` | ported | `SnsSqs/SnsPublishBatchDeliversOneEnvelopePerEntryToTheSubscribedQueue.cs` |
| `sns__sqs.sns-sqs-queue-policy-deny-suppresses-delivery` | `4df2823` | ported | `SnsSqs/SnsSqsQueuePolicyDenySuppressesDelivery.cs` |
| `sns__sqs.sns-subscription-dlq-captures-the-notification-the-endpoint-policy-denied` | `4df2823` | ported | `SnsSqs/SnsSubscriptionDlqCapturesTheNotificationTheEndpointPolicyDenied.cs` |
| `sns__sqs.sns-to-sqs-envelope-carries-the-subject-and-typed-message-attributes` | `4df2823` | ported | `SnsSqs/SnsToSqsEnvelopeCarriesTheSubjectAndTypedMessageAttributes.cs` |
| `sns__sqs.sqs-envelope-raw-bytes-are-pretty-printed-in-canonical-key-order` | `4df2823` | ported | `SnsSqs/SqsEnvelopeRawBytesArePrettyPrintedInCanonicalKeyOrder.cs` |

## behavioral/sqs

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `sqs.add-permission-invalid-action-rejected` | `4df2823` | ported | `Sqs/AddPermissionInvalidActionRejected.cs` |
| `sqs.add-permission-over-limit-rejected` | `4df2823` | ported | `Sqs/AddPermissionOverLimitRejected.cs` |
| `sqs.add-remove-permission` | `4df2823` | ported | `Sqs/AddRemovePermission.cs` |
| `sqs.add-remove-permission-updates-policy` | `4df2823` | ported | `Sqs/AddRemovePermissionUpdatesPolicy.cs` |
| `sqs.approx-count-tracks` | `4df2823` | ported | `Sqs/ApproxCountTracks.cs` |
| `sqs.approximate-counters-converge-through-receive-release-and-delete` | `4df2823` | ported | `Sqs/ApproximateCountersConvergeThroughReceiveReleaseAndDelete.cs` |
| `sqs.approximate-receive-count-increments` | `4df2823` | ported | `Sqs/ApproximateReceiveCountIncrements.cs` |
| `sqs.bad-receipt-handle` | `4df2823` | ported | `Sqs/BadReceiptHandle.cs` |
| `sqs.batch-actions-authorize-under-their-singular-action` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.batch-entry-id-character-and-length-rules-rejected` | `4df2823` | ported | `Sqs/BatchEntryIdCharacterAndLengthRulesRejected.cs` |
| `sqs.batch-entry-over-queue-max-message-size-fails-alone` | `4df2823` | ported | `Sqs/BatchEntryOverQueueMaxMessageSizeFailsAlone.cs` |
| `sqs.batch-partial-failure-envelope-fields` | `4df2823` | ported | `Sqs/BatchPartialFailureEnvelopeFields.cs` |
| `sqs.batch-request-validation-rejected` | `4df2823` | ported | `Sqs/BatchRequestValidationRejected.cs` |
| `sqs.batch-send-partial-failure` | `4df2823` | ported | `Sqs/BatchSendPartialFailure.cs` |
| `sqs.batch-send-partial-mixed` | `4df2823` | ported | `Sqs/BatchSendPartialMixed.cs` |
| `sqs.batch-send-receive-count` | `4df2823` | ported | `Sqs/BatchSendReceiveCount.cs` |
| `sqs.batch-total-size-counts-message-attributes-not-only-bodies` | `4df2823` | ported | `Sqs/BatchTotalSizeCountsMessageAttributesNotOnlyBodies.cs` |
| `sqs.batch-total-size-exceeds-limit-rejected` | `4df2823` | ported | `Sqs/BatchTotalSizeExceedsLimitRejected.cs` |
| `sqs.binary-message-attribute` | `4df2823` | ported | `Sqs/BinaryMessageAttribute.cs` |
| `sqs.cancel-message-move-task-conserves-messages` | `4df2823` | ported | `Sqs/CancelMessageMoveTaskConservesMessages.cs` |
| `sqs.change-message-visibility-batch` | `4df2823` | ported | `Sqs/ChangeMessageVisibilityBatch.cs` |
| `sqs.change-message-visibility-batch-partial` | `4df2823` | ported | `Sqs/ChangeMessageVisibilityBatchPartial.cs` |
| `sqs.change-message-visibility-batch-partial-failure` | `4df2823` | ported | `Sqs/ChangeMessageVisibilityBatchPartialFailure.cs` |
| `sqs.change-message-visibility-batch-repeated-receipt-handle-last-entry-wins` | `4df2823` | ported | `Sqs/ChangeMessageVisibilityBatchRepeatedReceiptHandleLastEntryWins.cs` |
| `sqs.change-message-visibility-expired-handle-accepted` | `4df2823` | ported | `Sqs/ChangeMessageVisibilityExpiredHandleAccepted.cs` |
| `sqs.change-message-visibility-extends` | `4df2823` | ported | `Sqs/ChangeMessageVisibilityExtends.cs` |
| `sqs.change-message-visibility-extends-timeout` | `4df2823` | ported | `Sqs/ChangeMessageVisibilityExtendsTimeout.cs` |
| `sqs.change-message-visibility-invalid-receipt-handle` | `4df2823` | ported | `Sqs/ChangeMessageVisibilityInvalidReceiptHandle.cs` |
| `sqs.change-message-visibility-zero-makes-immediately-visible` | `4df2823` | ported | `Sqs/ChangeMessageVisibilityZeroMakesImmediatelyVisible.cs` |
| `sqs.change-visibility-on-expired-handle-rejected` | `4df2823` | ported | `Sqs/ChangeVisibilityOnExpiredHandleRejected.cs` |
| `sqs.changevis0-reexposes` | `4df2823` | ported | `Sqs/Changevis0Reexposes.cs` |
| `sqs.clearing-kms-master-key-id-does-not-restore-sse-sqs` | `4df2823` | not ported | KMS keys |
| `sqs.cli-output-text-mode` | `4df2823` | ported | `Sqs/CliOutputTextMode.cs` |
| `sqs.cli-query-jmespath-filter` | `4df2823` | ported | `Sqs/CliQueryJmespathFilter.cs` |
| `sqs.cli-script-sqs-fanout` | `4df2823` | ported | `Sqs/CliScriptSqsFanout.cs` |
| `sqs.cli-sqs-create-send-receive-delete` | `4df2823` | ported | `Sqs/CliSqsCreateSendReceiveDelete.cs` |
| `sqs.cli-sqs-nonexistent-queue-error` | `4df2823` | ported | `Sqs/CliSqsNonexistentQueueError.cs` |
| `sqs.client-on-the-queue-url-host-sends-and-receives-on-that-queue` | `4df2823` | ported | `Sqs/ClientOnTheQueueUrlHostSendsAndReceivesOnThatQueue.cs` |
| `sqs.cloudwatch-alarm-state-transition-recorded` | `4df2823` | not ported | CloudWatch alarms |
| `sqs.conc-limits-and-fanout-fifo-stale-owner-handle-rejected-after-handoff` | `4df2823` | ported | `Sqs/ConcLimitsAndFanoutFifoStaleOwnerHandleRejectedAfterHandoff.cs` |
| `sqs.conc-limits-and-fanout-in-flight-disjoint-and-cmv-zero-redelivers-exact-subset` | `4df2823` | ported | `Sqs/ConcLimitsAndFanoutInFlightDisjointAndCmvZeroRedeliversExactSubset.cs` |
| `sqs.concurrent-create-queue-same-name-converges-to-one-queue` | `4df2823` | ported | `Sqs/ConcurrentCreateQueueSameNameConvergesToOneQueue.cs` |
| `sqs.concurrent-fifo-dedup-race` | `4df2823` | ported | `Sqs/ConcurrentFifoDedupRace.cs` |
| `sqs.concurrent-receive-no-duplicate-delivery` | `4df2823` | ported | `Sqs/ConcurrentReceiveNoDuplicateDelivery.cs` |
| `sqs.concurrent-set-queue-attributes-last-writer-wins` | `4df2823` | ported | `Sqs/ConcurrentSetQueueAttributesLastWriterWins.cs` |
| `sqs.concurrent-set-queue-attributes-on-distinct-attributes-lose-no-update` | `4df2823` | ported | `Sqs/ConcurrentSetQueueAttributesOnDistinctAttributesLoseNoUpdate.cs` |
| `sqs.concurrent-tag-queue-no-lost-updates` | `4df2823` | ported | `Sqs/ConcurrentTagQueueNoLostUpdates.cs` |
| `sqs.content-based-dedup-id-is-the-sha256-of-the-body` | `4df2823` | ported | `Sqs/ContentBasedDedupIdIsTheSha256OfTheBody.cs` |
| `sqs.content-based-deduplication-toggled-on-live-fifo-queue` | `4df2823` | ported | `Sqs/ContentBasedDeduplicationToggledOnLiveFifoQueue.cs` |
| `sqs.create-fifo-queue-after-set-queue-attributes-compares-current-values` | `4df2823` | ported | `Sqs/CreateFifoQueueAfterSetQueueAttributesComparesCurrentValues.cs` |
| `sqs.create-fifo-queue-without-suffix-fails` | `4df2823` | ported | `Sqs/CreateFifoQueueWithoutSuffixFails.cs` |
| `sqs.create-queue-after-set-queue-attributes-compares-current-values` | `4df2823` | ported | `Sqs/CreateQueueAfterSetQueueAttributesComparesCurrentValues.cs` |
| `sqs.create-queue-existing-name-different-policy-not-conflicting` | `4df2823` | ported | `Sqs/CreateQueueExistingNameDifferentPolicyNotConflicting.cs` |
| `sqs.create-queue-existing-name-different-tags-rejected` | `4df2823` | ported | `Sqs/CreateQueueExistingNameDifferentTagsRejected.cs` |
| `sqs.create-queue-invalid-attribute-creates-nothing-rejected` | `4df2823` | ported | `Sqs/CreateQueueInvalidAttributeCreatesNothingRejected.cs` |
| `sqs.create-queue-name-conflict-different-attributes-rejected` | `4df2823` | ported | `Sqs/CreateQueueNameConflictDifferentAttributesRejected.cs` |
| `sqs.dead-letter-queue-redrive-after-max-receive-count` | `4df2823` | ported | `Sqs/DeadLetterQueueRedriveAfterMaxReceiveCount.cs` |
| `sqs.dead-letter-wiring-to-another-region-rejected` | `4df2823` | ported | `Sqs/DeadLetterWiringToAnotherRegionRejected.cs` |
| `sqs.dead-lettered-message-keeps-its-identity-and-names-its-source-queue` | `4df2823` | ported | `Sqs/DeadLetteredMessageKeepsItsIdentityAndNamesItsSourceQueue.cs` |
| `sqs.default-max-message-size-is-one-mib-boundary` | `4df2823` | ported | `Sqs/DefaultMaxMessageSizeIsOneMibBoundary.cs` |
| `sqs.delay-queue` | `4df2823` | ported | `Sqs/DelayQueue.cs` |
| `sqs.delayed-messages-are-counted-separately-from-visible-and-in-flight` | `4df2823` | ported | `Sqs/DelayedMessagesAreCountedSeparatelyFromVisibleAndInFlight.cs` |
| `sqs.delete-and-visibility-batches-duplicate-ids-rejected` | `4df2823` | ported | `Sqs/DeleteAndVisibilityBatchesDuplicateIdsRejected.cs` |
| `sqs.delete-and-visibility-batches-over-ten-entries-rejected` | `4df2823` | ported | `Sqs/DeleteAndVisibilityBatchesOverTenEntriesRejected.cs` |
| `sqs.delete-and-visibility-batches-receipt-handle-from-another-queue-fails-alone` | `4df2823` | ported | `Sqs/DeleteAndVisibilityBatchesReceiptHandleFromAnotherQueueFailsAlone.cs` |
| `sqs.delete-message-batch` | `4df2823` | ported | `Sqs/DeleteMessageBatch.cs` |
| `sqs.delete-message-batch-empty-request` | `4df2823` | ported | `Sqs/DeleteMessageBatchEmptyRequest.cs` |
| `sqs.delete-message-batch-partial-failure` | `4df2823` | ported | `Sqs/DeleteMessageBatchPartialFailure.cs` |
| `sqs.delete-message-invalid-receipt-handle-rejected` | `4df2823` | ported | `Sqs/DeleteMessageInvalidReceiptHandleRejected.cs` |
| `sqs.delete-message-repeated-with-the-same-receipt-handle-succeeds` | `4df2823` | ported | `Sqs/DeleteMessageRepeatedWithTheSameReceiptHandleSucceeds.cs` |
| `sqs.delete-message-stale-receipt-handle-after-redelivery-succeeds` | `4df2823` | ported | `Sqs/DeleteMessageStaleReceiptHandleAfterRedeliverySucceeds.cs` |
| `sqs.delete-then-empty` | `4df2823` | ported | `Sqs/DeleteThenEmpty.cs` |
| `sqs.deploy-cfn-attribute-update-takes-effect` | `4df2823` | not ported | CloudFormation stacks |
| `sqs.deploy-cfn-dlq-redrive-behaves` | `4df2823` | not ported | CloudFormation stacks |
| `sqs.deploy-cfn-encryption-update-keeps-earlier-messages-readable` | `4df2823` | not ported | CloudFormation stacks |
| `sqs.deploy-cfn-fifo-dlq-redrive-allow-policy-admits-only-the-stack-source` | `4df2823` | not ported | CloudFormation stacks |
| `sqs.deploy-cfn-fifo-queue-dedup-behaves` | `4df2823` | not ported | CloudFormation stacks |
| `sqs.deploy-cfn-kms-queue-inline-policy-enforces-key-and-queue-grants` | `4df2823` | not ported | CloudFormation stacks |
| `sqs.deploy-cfn-queue-behaves` | `4df2823` | not ported | CloudFormation stacks |
| `sqs.deploy-cfn-queue-invalid-message-contents-rejected` | `4df2823` | not ported | CloudFormation stacks |
| `sqs.deploy-cfn-queue-policy-grants-role-and-update-revokes` | `4df2823` | not ported | CloudFormation stacks |
| `sqs.deploy-cfn-stack-delete-removes-queue` | `4df2823` | not ported | CloudFormation stacks |
| `sqs.dlq-move-preserves-standard-enqueue-timestamp-and-resets-it-for-fifo` | `4df2823` | ported | `Sqs/DlqMovePreservesStandardEnqueueTimestampAndResetsItForFifo.cs` |
| `sqs.dlq-move-withheld-until-the-maxreceivecount-th-delivery` | `4df2823` | ported | `Sqs/DlqMoveWithheldUntilTheMaxreceivecountThDelivery.cs` |
| `sqs.dlq-moves-only-the-undeleted-message-after-four-deliveries` | `4df2823` | ported | `Sqs/DlqMovesOnlyTheUndeletedMessageAfterFourDeliveries.cs` |
| `sqs.dlq-redrive` | `4df2823` | ported | `Sqs/DlqRedrive.cs` |
| `sqs.duplicate-batch-ids` | `4df2823` | ported | `Sqs/DuplicateBatchIds.cs` |
| `sqs.e2e-delay-queue-then-consume` | `4df2823` | ported | `Sqs/E2eDelayQueueThenConsume.cs` |
| `sqs.e2e-dlq-redrive-full-cycle` | `4df2823` | ported | `Sqs/E2eDlqRedriveFullCycle.cs` |
| `sqs.e2e-permission-boundary-then-revoke` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.e2e-work-queue-pattern` | `4df2823` | ported | `Sqs/E2eWorkQueuePattern.cs` |
| `sqs.empty-collection-parameters-validated-inconsistently` | `4df2823` | known gap: Other | `Sqs/EmptyCollectionParametersValidatedInconsistently.cs` |
| `sqs.empty-message-body` | `4df2823` | ported | `Sqs/EmptyMessageBody.cs` |
| `sqs.empty-vs-absent-message-attribute-preserved` | `4df2823` | ported | `Sqs/EmptyVsAbsentMessageAttributePreserved.cs` |
| `sqs.fifo-batch-with-a-group-less-entry-rejected-whole-and-group-stream-unchanged` | `4df2823` | ported | `Sqs/FifoBatchWithAGroupLessEntryRejectedWholeAndGroupStreamUnchanged.cs` |
| `sqs.fifo-concurrent-senders-preserve-per-sender-order` | `4df2823` | ported | `Sqs/FifoConcurrentSendersPreservePerSenderOrder.cs` |
| `sqs.fifo-content-based-dedup` | `4df2823` | ported | `Sqs/FifoContentBasedDedup.cs` |
| `sqs.fifo-content-based-dedup-ignores-attributes` | `4df2823` | ported | `Sqs/FifoContentBasedDedupIgnoresAttributes.cs` |
| `sqs.fifo-dead-letter-queue-move-sets-dedup-id-to-original-message-id` | `4df2823` | ported | `Sqs/FifoDeadLetterQueueMoveSetsDedupIdToOriginalMessageId.cs` |
| `sqs.fifo-dedup` | `4df2823` | ported | `Sqs/FifoDedup.cs` |
| `sqs.fifo-dedup-id-reusable-after-dedup-window-elapses` | `4df2823` | ported | `Sqs/FifoDedupIdReusableAfterDedupWindowElapses.cs` |
| `sqs.fifo-dedup-scope-message-group-allows-same-dedup-id-in-different-groups` | `4df2823` | ported | `Sqs/FifoDedupScopeMessageGroupAllowsSameDedupIdInDifferentGroups.cs` |
| `sqs.fifo-dedup-survives-consumer-delete` | `4df2823` | ported | `Sqs/FifoDedupSurvivesConsumerDelete.cs` |
| `sqs.fifo-exactly-once-multiple-sends` | `4df2823` | ported | `Sqs/FifoExactlyOnceMultipleSends.cs` |
| `sqs.fifo-group-blocked-by-a-poison-message-resumes-after-the-dlq-move` | `4df2823` | ported | `Sqs/FifoGroupBlockedByAPoisonMessageResumesAfterTheDlqMove.cs` |
| `sqs.fifo-group-held-for-the-extended-visibility-timeout-not-the-original` | `4df2823` | ported | `Sqs/FifoGroupHeldForTheExtendedVisibilityTimeoutNotTheOriginal.cs` |
| `sqs.fifo-group-isolation` | `4df2823` | ported | `Sqs/FifoGroupIsolation.cs` |
| `sqs.fifo-group-less-send-rejected-without-consuming-its-deduplication-id` | `4df2823` | ported | `Sqs/FifoGroupLessSendRejectedWithoutConsumingItsDeduplicationId.cs` |
| `sqs.fifo-group-natural-visibility-expiry-keeps-group-held-and-redelivers-same-head` | `4df2823` | ported | `Sqs/FifoGroupNaturalVisibilityExpiryKeepsGroupHeldAndRedeliversSameHead.cs` |
| `sqs.fifo-message-group-held-by-one-concurrent-receiver-at-a-time` | `4df2823` | ported | `Sqs/FifoMessageGroupHeldByOneConcurrentReceiverAtATime.cs` |
| `sqs.fifo-missing-group-id` | `4df2823` | ported | `Sqs/FifoMissingGroupId.cs` |
| `sqs.fifo-missing-message-group-id-rejected` | `4df2823` | ported | `Sqs/FifoMissingMessageGroupIdRejected.cs` |
| `sqs.fifo-ordering` | `4df2823` | ported | `Sqs/FifoOrdering.cs` |
| `sqs.fifo-per-group-order-preserved-under-concurrent-receivers` | `4df2823` | ported | `Sqs/FifoPerGroupOrderPreservedUnderConcurrentReceivers.cs` |
| `sqs.fifo-per-message-delay-seconds-rejected` | `4df2823` | ported | `Sqs/FifoPerMessageDelaySecondsRejected.cs` |
| `sqs.fifo-queue-moves-an-exact-multibyte-message-and-the-deleted-fifo-queue-is-gone` | `4df2823` | ported | `Sqs/FifoQueueMovesAnExactMultibyteMessageAndTheDeletedFifoQueueIsGone.cs` |
| `sqs.fifo-receive-request-attempt-id-replays-batch` | `4df2823` | ported | `Sqs/FifoReceiveRequestAttemptIdReplaysBatch.cs` |
| `sqs.fifo-send-message-batch-shared-dedup-id-first-entry-wins` | `4df2823` | ported | `Sqs/FifoSendMessageBatchSharedDedupIdFirstEntryWins.cs` |
| `sqs.fifo-send-without-dedup-id-fails` | `4df2823` | ported | `Sqs/FifoSendWithoutDedupIdFails.cs` |
| `sqs.fifo-send-without-message-group-id-rejected-and-group-stream-unchanged` | `4df2823` | ported | `Sqs/FifoSendWithoutMessageGroupIdRejectedAndGroupStreamUnchanged.cs` |
| `sqs.fifo-sequence-number-monotonic-within-group` | `4df2823` | ported | `Sqs/FifoSequenceNumberMonotonicWithinGroup.cs` |
| `sqs.fifo-throughput-limit-per-message-group-requires-message-group-scope-rejected` | `4df2823` | ported | `Sqs/FifoThroughputLimitPerMessageGroupRequiresMessageGroupScopeRejected.cs` |
| `sqs.fifo-token-ids-limited-to-128-printable-characters-rejected` | `4df2823` | ported | `Sqs/FifoTokenIdsLimitedTo128PrintableCharactersRejected.cs` |
| `sqs.fifo-twenty-group-head-of-line-isolation-at-volume` | `4df2823` | ported | `Sqs/FifoTwentyGroupHeadOfLineIsolationAtVolume.cs` |
| `sqs.get-queue-attributes-invalid-attribute-name` | `4df2823` | ported | `Sqs/GetQueueAttributesInvalidAttributeName.cs` |
| `sqs.get-queue-attributes-invalid-attribute-name-rejected` | `4df2823` | ported | `Sqs/GetQueueAttributesInvalidAttributeNameRejected.cs` |
| `sqs.get-queue-url-does-not-format-validate-the-name` | `4df2823` | ported | `Sqs/GetQueueUrlDoesNotFormatValidateTheName.cs` |
| `sqs.get-queue-url-with-the-owner-account-id-resolves-the-queue-and-a-missing-name-rejected` | `4df2823` | ported | `Sqs/GetQueueUrlWithTheOwnerAccountIdResolvesTheQueueAndAMissingNameRejected.cs` |
| `sqs.identity-policy-requesttag-condition-gates-create-queue` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.identity-policy-resourcetag-condition-gates-send-message` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.immutable-queue-arn-mutation-rejected` | `4df2823` | ported | `Sqs/ImmutableQueueArnMutationRejected.cs` |
| `sqs.kms-data-key-reuse-period-bounds-rejected` | `4df2823` | not ported | KMS keys |
| `sqs.kms-key-permission-gates-send-message` | `4df2823` | not ported | KMS keys |
| `sqs.list-dead-letter-source-queues-paginates` | `4df2823` | ported | `Sqs/ListDeadLetterSourceQueuesPaginates.cs` |
| `sqs.list-dead-letter-source-queues-reflects-redrive-policy` | `4df2823` | ported | `Sqs/ListDeadLetterSourceQueuesReflectsRedrivePolicy.cs` |
| `sqs.list-message-move-tasks-max-results-out-of-range-rejected` | `4df2823` | ported | `Sqs/ListMessageMoveTasksMaxResultsOutOfRangeRejected.cs` |
| `sqs.list-queues-max-results-paginates-with-next-token` | `4df2823` | ported | `Sqs/ListQueuesMaxResultsPaginatesWithNextToken.cs` |
| `sqs.list-queues-pagination-bad-input-rejected` | `4df2823` | ported | `Sqs/ListQueuesPaginationBadInputRejected.cs` |
| `sqs.list-queues-prefix-matches-only-at-start` | `4df2823` | ported | `Sqs/ListQueuesPrefixMatchesOnlyAtStart.cs` |
| `sqs.list-queues-without-max-results-omits-next-token` | `4df2823` | ported | `Sqs/ListQueuesWithoutMaxResultsOmitsNextToken.cs` |
| `sqs.long-poll-blocks-on-empty` | `4df2823` | ported | `Sqs/LongPollBlocksOnEmpty.cs` |
| `sqs.long-poll-returns-immediately-when-message-already-available` | `4df2823` | ported | `Sqs/LongPollReturnsImmediatelyWhenMessageAlreadyAvailable.cs` |
| `sqs.long-poll-wakes-when-delay-or-visibility-expires` | `4df2823` | ported | `Sqs/LongPollWakesWhenDelayOrVisibilityExpires.cs` |
| `sqs.long-polling-returns-fast` | `4df2823` | ported | `Sqs/LongPollingReturnsFast.cs` |
| `sqs.long-polling-wakes-on-message-arrival` | `4df2823` | ported | `Sqs/LongPollingWakesOnMessageArrival.cs` |
| `sqs.max-message-size` | `4df2823` | ported | `Sqs/MaxMessageSize.cs` |
| `sqs.max-message-size-lowered-spares-already-enqueued-messages` | `4df2823` | ported | `Sqs/MaxMessageSizeLoweredSparesAlreadyEnqueuedMessages.cs` |
| `sqs.max-size-message-body-round-trip` | `4df2823` | ported | `Sqs/MaxSizeMessageBodyRoundTrip.cs` |
| `sqs.maximum-message-size-is-a-byte-boundary-not-a-character-count` | `4df2823` | ported | `Sqs/MaximumMessageSizeIsAByteBoundaryNotACharacterCount.cs` |
| `sqs.md5-of-message-attributes` | `4df2823` | ported | `Sqs/Md5OfMessageAttributes.cs` |
| `sqs.message-attribute-name-rules-rejected` | `4df2823` | ported | `Sqs/MessageAttributeNameRulesRejected.cs` |
| `sqs.message-attribute-names-differing-only-in-case-are-distinct` | `4df2823` | ported | `Sqs/MessageAttributeNamesDifferingOnlyInCaseAreDistinct.cs` |
| `sqs.message-attribute-type-and-number-value-validated` | `4df2823` | ported | `Sqs/MessageAttributeTypeAndNumberValueValidated.cs` |
| `sqs.message-attribute-type-length-bounds-rejected` | `4df2823` | ported | `Sqs/MessageAttributeTypeLengthBoundsRejected.cs` |
| `sqs.message-attribute-value-in-the-wrong-field-for-its-data-type-rejected` | `4df2823` | ported | `Sqs/MessageAttributeValueInTheWrongFieldForItsDataTypeRejected.cs` |
| `sqs.message-attributes-round-trip-types` | `4df2823` | ported | `Sqs/MessageAttributesRoundTripTypes.cs` |
| `sqs.message-attributes-round-trip-with-md5` | `4df2823` | ported | `Sqs/MessageAttributesRoundTripWithMd5.cs` |
| `sqs.message-attributes-roundtrip` | `4df2823` | ported | `Sqs/MessageAttributesRoundtrip.cs` |
| `sqs.message-digests-recomputed-from-the-documented-canonical-encoding` | `4df2823` | ported | `Sqs/MessageDigestsRecomputedFromTheDocumentedCanonicalEncoding.cs` |
| `sqs.message-move-task-across-regions-rejected` | `4df2823` | ported | `Sqs/MessageMoveTaskAcrossRegionsRejected.cs` |
| `sqs.message-move-task-completes-and-drains-dlq` | `4df2823` | ported | `Sqs/MessageMoveTaskCompletesAndDrainsDlq.cs` |
| `sqs.message-move-task-redrive-assigns-new-message-id-and-sent-timestamp` | `4df2823` | ported | `Sqs/MessageMoveTaskRedriveAssignsNewMessageIdAndSentTimestamp.cs` |
| `sqs.message-move-task-redrives-dlq-back-to-source` | `4df2823` | ported | `Sqs/MessageMoveTaskRedrivesDlqBackToSource.cs` |
| `sqs.message-retention-period-expires-old-messages` | `4df2823` | ported | `Sqs/MessageRetentionPeriodExpiresOldMessages.cs` |
| `sqs.message-retention-period-expires-undeleted-message` | `4df2823` | ported | `Sqs/MessageRetentionPeriodExpiresUndeletedMessage.cs` |
| `sqs.message-size-counts-attribute-name-type-and-value` | `4df2823` | ported | `Sqs/MessageSizeCountsAttributeNameTypeAndValue.cs` |
| `sqs.message-system-attribute-other-than-aws-trace-header-rejected` | `4df2823` | ported | `Sqs/MessageSystemAttributeOtherThanAwsTraceHeaderRejected.cs` |
| `sqs.message-timer` | `4df2823` | ported | `Sqs/MessageTimer.cs` |
| `sqs.more-than-ten-message-attributes-rejected` | `4df2823` | ported | `Sqs/MoreThanTenMessageAttributesRejected.cs` |
| `sqs.nonexistent-queue` | `4df2823` | ported | `Sqs/NonexistentQueue.cs` |
| `sqs.numeric-message-attribute-precision-preserved` | `4df2823` | ported | `Sqs/NumericMessageAttributePrecisionPreserved.cs` |
| `sqs.op-cancel-unknown-move-task-rejected` | `4df2823` | ported | `Sqs/OpCancelUnknownMoveTaskRejected.cs` |
| `sqs.op-delay-seconds-out-of-range-rejected` | `4df2823` | ported | `Sqs/OpDelaySecondsOutOfRangeRejected.cs` |
| `sqs.op-fifo-attributes-on-standard-queue-rejected` | `4df2823` | ported | `Sqs/OpFifoAttributesOnStandardQueueRejected.cs` |
| `sqs.op-message-move-task-on-non-dlq-rejected` | `4df2823` | ported | `Sqs/OpMessageMoveTaskOnNonDlqRejected.cs` |
| `sqs.op-queue-name-validation-rejected` | `4df2823` | ported | `Sqs/OpQueueNameValidationRejected.cs` |
| `sqs.op-receive-message-param-bounds-rejected` | `4df2823` | ported | `Sqs/OpReceiveMessageParamBoundsRejected.cs` |
| `sqs.op-send-message-oversize-rejected` | `4df2823` | ported | `Sqs/OpSendMessageOversizeRejected.cs` |
| `sqs.op-tag-and-permission-validation-rejected` | `4df2823` | ported | `Sqs/OpTagAndPermissionValidationRejected.cs` |
| `sqs.op-visibility-timeout-out-of-range-rejected` | `4df2823` | ported | `Sqs/OpVisibilityTimeoutOutOfRangeRejected.cs` |
| `sqs.operations-fail-after-delete` | `4df2823` | ported | `Sqs/OperationsFailAfterDelete.cs` |
| `sqs.overly-permissive-cross-account-receive-denied` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.overly-permissive-delete-queue-without-permission-denied` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.overly-permissive-send-without-permission-denied` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.per-message-delay-seconds-overrides-queue-delay-both-directions` | `4df2823` | ported | `Sqs/PerMessageDelaySecondsOverridesQueueDelayBothDirections.cs` |
| `sqs.purge-empties` | `4df2823` | ported | `Sqs/PurgeEmpties.cs` |
| `sqs.purge-queue-discards-in-flight-messages` | `4df2823` | ported | `Sqs/PurgeQueueDiscardsInFlightMessages.cs` |
| `sqs.purge-queue-in-progress-rejected` | `4df2823` | known gap: Validation | `Sqs/PurgeQueueInProgressRejected.cs` |
| `sqs.purge-queue-removes-all-messages` | `4df2823` | ported | `Sqs/PurgeQueueRemovesAllMessages.cs` |
| `sqs.purge-queue-resets-the-fifo-dedup-cache` | `4df2823` | ported | `Sqs/PurgeQueueResetsTheFifoDedupCache.cs` |
| `sqs.query-and-json-protocol-clients-share-one-queue` | `4df2823` | ported | `Sqs/QueryAndJsonProtocolClientsShareOneQueue.cs` |
| `sqs.query-compatible-error-carriers-differ-by-consumer-rejected` | `4df2823` | not ported | Lambda functions, with IAM roles |
| `sqs.query-protocol-refusals-are-xml-error-responses-rejected` | `4df2823` | ported | `Sqs/QueryProtocolRefusalsAreXmlErrorResponsesRejected.cs` |
| `sqs.queue-arn-and-get-queue-url-consistency` | `4df2823` | ported | `Sqs/QueueArnAndGetQueueUrlConsistency.cs` |
| `sqs.queue-arn-carries-the-creation-region` | `4df2823` | ported | `Sqs/QueueArnCarriesTheCreationRegion.cs` |
| `sqs.queue-arn-stable-round-trip` | `4df2823` | ported | `Sqs/QueueArnStableRoundTrip.cs` |
| `sqs.queue-attribute-range-bounds-rejected` | `4df2823` | ported | `Sqs/QueueAttributeRangeBoundsRejected.cs` |
| `sqs.queue-attributes-roundtrip` | `4df2823` | ported | `Sqs/QueueAttributesRoundtrip.cs` |
| `sqs.queue-create-idempotent-same-attributes` | `4df2823` | ported | `Sqs/QueueCreateIdempotentSameAttributes.cs` |
| `sqs.queue-delay-change-is-retroactive-for-fifo-but-not-standard` | `4df2823` | ported | `Sqs/QueueDelayChangeIsRetroactiveForFifoButNotStandard.cs` |
| `sqs.queue-delay-seconds-attribute-bounds-rejected` | `4df2823` | ported | `Sqs/QueueDelaySecondsAttributeBoundsRejected.cs` |
| `sqs.queue-deleted-recently-on-fast-recreate-rejected` | `4df2823` | known gap: Validation | `Sqs/QueueDeletedRecentlyOnFastRecreateRejected.cs` |
| `sqs.queue-level-wait-time` | `4df2823` | ported | `Sqs/QueueLevelWaitTime.cs` |
| `sqs.queue-level-wait-time-wakes-on-arrival` | `4df2823` | ported | `Sqs/QueueLevelWaitTimeWakesOnArrival.cs` |
| `sqs.queue-moves-a-batch-of-exact-messages-and-the-deleted-queue-refuses-batch-calls` | `4df2823` | ported | `Sqs/QueueMovesABatchOfExactMessagesAndTheDeletedQueueRefusesBatchCalls.cs` |
| `sqs.queue-moves-an-exact-message-and-the-deleted-queue-is-gone` | `4df2823` | ported | `Sqs/QueueMovesAnExactMessageAndTheDeletedQueueIsGone.cs` |
| `sqs.queue-name-eighty-character-boundary-rejected` | `4df2823` | ported | `Sqs/QueueNameEightyCharacterBoundaryRejected.cs` |
| `sqs.queue-name-exists` | `4df2823` | ported | `Sqs/QueueNameExists.cs` |
| `sqs.queue-name-exists-conflicting-attributes-rejected` | `4df2823` | ported | `Sqs/QueueNameExistsConflictingAttributesRejected.cs` |
| `sqs.queue-name-resolution-is-case-sensitive-and-exact` | `4df2823` | ported | `Sqs/QueueNameResolutionIsCaseSensitiveAndExact.cs` |
| `sqs.queue-policy-action-names-outside-the-sqs-action-list-rejected` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.queue-policy-attribute-normalizes-principal-and-clears` | `4df2823` | ported | `Sqs/QueuePolicyAttributeNormalizesPrincipalAndClears.cs` |
| `sqs.queue-policy-explicit-deny-blocks-send` | `4df2823` | ported | `Sqs/QueuePolicyExplicitDenyBlocksSend.cs` |
| `sqs.queue-policy-naming-nonexistent-principal-rejected` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.queue-policy-naming-the-same-queue-name-in-another-region-grants-nothing` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.queue-policy-principal-tag-condition-gates-send` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.queue-policy-send-grant-does-not-authorize-receive-or-delete` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.queue-policy-source-arn-condition-denies-a-direct-caller` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.queue-policy-statement-for-another-queue-grants-nothing` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.queue-url-from-another-region-not-served-rejected` | `4df2823` | ported | `Sqs/QueueUrlFromAnotherRegionNotServedRejected.cs` |
| `sqs.queue-visibility-timeout-change-spares-in-flight-messages` | `4df2823` | ported | `Sqs/QueueVisibilityTimeoutChangeSparesInFlightMessages.cs` |
| `sqs.random-body-roundtrip` | `4df2823` | ported | `Sqs/RandomBodyRoundtrip.cs` |
| `sqs.raw-json-post-missing-target-or-mismatched-content-type-rejected` | `4df2823` | ported | `Sqs/RawJsonPostMissingTargetOrMismatchedContentTypeRejected.cs` |
| `sqs.receipt-handle-changes-per-receive` | `4df2823` | ported | `Sqs/ReceiptHandleChangesPerReceive.cs` |
| `sqs.receipt-handle-from-a-deleted-queue-incarnation-rejected` | `4df2823` | ported | `Sqs/ReceiptHandleFromADeletedQueueIncarnationRejected.cs` |
| `sqs.receipt-handle-from-another-queue-rejected` | `4df2823` | ported | `Sqs/ReceiptHandleFromAnotherQueueRejected.cs` |
| `sqs.receive-count-and-first-receive-timestamp-invariants-across-redeliveries` | `4df2823` | ported | `Sqs/ReceiveCountAndFirstReceiveTimestampInvariantsAcrossRedeliveries.cs` |
| `sqs.receive-count-increments` | `4df2823` | ported | `Sqs/ReceiveCountIncrements.cs` |
| `sqs.receive-hides` | `4df2823` | ported | `Sqs/ReceiveHides.cs` |
| `sqs.receive-max-messages` | `4df2823` | ported | `Sqs/ReceiveMaxMessages.cs` |
| `sqs.receive-message-attribute-name-wildcard-selects-by-name-prefix` | `4df2823` | ported | `Sqs/ReceiveMessageAttributeNameWildcardSelectsByNamePrefix.cs` |
| `sqs.receive-message-attribute-selectivity` | `4df2823` | ported | `Sqs/ReceiveMessageAttributeSelectivity.cs` |
| `sqs.receive-message-respects-max-number-of-messages-cap` | `4df2823` | ported | `Sqs/ReceiveMessageRespectsMaxNumberOfMessagesCap.cs` |
| `sqs.receive-message-visibility-timeout-override-per-call` | `4df2823` | ported | `Sqs/ReceiveMessageVisibilityTimeoutOverridePerCall.cs` |
| `sqs.receive-request-attempt-id-not-retryable-after-batch-modified-rejected` | `4df2823` | ported | `Sqs/ReceiveRequestAttemptIdNotRetryableAfterBatchModifiedRejected.cs` |
| `sqs.receive-request-attempt-id-replay-resets-visibility-timeout` | `4df2823` | ported | `Sqs/ReceiveRequestAttemptIdReplayResetsVisibilityTimeout.cs` |
| `sqs.receive-wait-time-zero-overrides-queue-default` | `4df2823` | ported | `Sqs/ReceiveWaitTimeZeroOverridesQueueDefault.cs` |
| `sqs.recreated-queue-does-not-inherit-the-deleted-queues-policy` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.redrive-allow-policy-by-queue-rejects-unlisted-source` | `4df2823` | ported | `Sqs/RedriveAllowPolicyByQueueRejectsUnlistedSource.cs` |
| `sqs.redrive-allow-policy-deny-all-rejects-dlq-association-rejected` | `4df2823` | ported | `Sqs/RedriveAllowPolicyDenyAllRejectsDlqAssociationRejected.cs` |
| `sqs.redrive-allow-policy-denyall-blocks-automatic-redrive-on-already-wired-source` | `4df2823` | ported | `Sqs/RedriveAllowPolicyDenyallBlocksAutomaticRedriveOnAlreadyWiredSource.cs` |
| `sqs.redrive-allow-policy-denyall-does-not-block-start-message-move-task` | `4df2823` | ported | `Sqs/RedriveAllowPolicyDenyallDoesNotBlockStartMessageMoveTask.cs` |
| `sqs.redrive-allow-policy-document-validation-rejected` | `4df2823` | ported | `Sqs/RedriveAllowPolicyDocumentValidationRejected.cs` |
| `sqs.redrive-policy-cleared-by-empty-string-unlinks-dlq` | `4df2823` | ported | `Sqs/RedrivePolicyClearedByEmptyStringUnlinksDlq.cs` |
| `sqs.redrive-policy-dlq-source-listing` | `4df2823` | ported | `Sqs/RedrivePolicyDlqSourceListing.cs` |
| `sqs.redrive-policy-dlq-type-mismatch-rejected` | `4df2823` | ported | `Sqs/RedrivePolicyDlqTypeMismatchRejected.cs` |
| `sqs.redrive-policy-max-receive-count-out-of-range-rejected` | `4df2823` | ported | `Sqs/RedrivePolicyMaxReceiveCountOutOfRangeRejected.cs` |
| `sqs.redrive-policy-missing-target-or-count-rejected` | `4df2823` | ported | `Sqs/RedrivePolicyMissingTargetOrCountRejected.cs` |
| `sqs.redriven-message-restarts-its-receive-count-and-dead-letters-again-at-maxreceivecount` | `4df2823` | ported | `Sqs/RedrivenMessageRestartsItsReceiveCountAndDeadLettersAgainAtMaxreceivecount.cs` |
| `sqs.remove-permission-nonexistent-label-rejected` | `4df2823` | ported | `Sqs/RemovePermissionNonexistentLabelRejected.cs` |
| `sqs.retention-expiry-discards-an-in-flight-message` | `4df2823` | ported | `Sqs/RetentionExpiryDiscardsAnInFlightMessage.cs` |
| `sqs.same-name-queues-in-two-regions-hold-separate-messages` | `4df2823` | ported | `Sqs/SameNameQueuesInTwoRegionsHoldSeparateMessages.cs` |
| `sqs.sdk-create-cli-read-sqs` | `4df2823` | ported | `Sqs/SdkCreateCliReadSqs.cs` |
| `sqs.send-message-batch-delay-seconds-applies-to-each-entry-alone` | `4df2823` | ported | `Sqs/SendMessageBatchDelaySecondsAppliesToEachEntryAlone.cs` |
| `sqs.send-message-batch-duplicate-ids-rejected-atomically` | `4df2823` | ported | `Sqs/SendMessageBatchDuplicateIdsRejectedAtomically.cs` |
| `sqs.send-message-batch-partial-failure` | `4df2823` | ported | `Sqs/SendMessageBatchPartialFailure.cs` |
| `sqs.send-message-batch-too-many-entries-rejected` | `4df2823` | ported | `Sqs/SendMessageBatchTooManyEntriesRejected.cs` |
| `sqs.send-message-invalid-body-character-rejected` | `4df2823` | ported | `Sqs/SendMessageInvalidBodyCharacterRejected.cs` |
| `sqs.send-message-malformed-awstraceheader-accepted-then-dropped` | `4df2823` | ported | `Sqs/SendMessageMalformedAwstraceheaderAcceptedThenDropped.cs` |
| `sqs.send-receive-delete-lifecycle` | `4df2823` | ported | `Sqs/SendReceiveDeleteLifecycle.cs` |
| `sqs.sender-id-and-sent-timestamp-derive-from-the-caller-and-the-live-clock` | `4df2823` | ported | `Sqs/SenderIdAndSentTimestampDeriveFromTheCallerAndTheLiveClock.cs` |
| `sqs.server-side-message-retention-auto-expunge` | `4df2823` | ported | `Sqs/ServerSideMessageRetentionAutoExpunge.cs` |
| `sqs.server-side-sse-kms-transparent-round-trip` | `4df2823` | not ported | KMS keys |
| `sqs.set-queue-attributes-invalid-attribute-name` | `4df2823` | ported | `Sqs/SetQueueAttributesInvalidAttributeName.cs` |
| `sqs.set-queue-attributes-invalid-policy-json-rejected` | `4df2823` | ported | `Sqs/SetQueueAttributesInvalidPolicyJsonRejected.cs` |
| `sqs.set-queue-attributes-one-invalid-value-applies-none-rejected` | `4df2823` | ported | `Sqs/SetQueueAttributesOneInvalidValueAppliesNoneRejected.cs` |
| `sqs.set-queue-attributes-policy-statement-limits-rejected` | `4df2823` | not ported | IAM roles, users and identity policies |
| `sqs.set-queue-attributes-propagates-to-get-queue-attributes` | `4df2823` | ported | `Sqs/SetQueueAttributesPropagatesToGetQueueAttributes.cs` |
| `sqs.set-queue-attributes-readonly-attribute-rejected` | `4df2823` | ported | `Sqs/SetQueueAttributesReadonlyAttributeRejected.cs` |
| `sqs.set-queue-attributes-roundtrip` | `4df2823` | ported | `Sqs/SetQueueAttributesRoundtrip.cs` |
| `sqs.short-poll-returns-immediately` | `4df2823` | ported | `Sqs/ShortPollReturnsImmediately.cs` |
| `sqs.sqsbatch-delete-duplicate-receipt-handle-deleted-once` | `4df2823` | ported | `Sqs/SqsbatchDeleteDuplicateReceiptHandleDeletedOnce.cs` |
| `sqs.sqsbatch-delete-full-width-one-bogus-handle-rejected` | `4df2823` | ported | `Sqs/SqsbatchDeleteFullWidthOneBogusHandleRejected.cs` |
| `sqs.sqsbatch-fifo-send-one-invalid-entry-rejects-whole-batch` | `4df2823` | ported | `Sqs/SqsbatchFifoSendOneInvalidEntryRejectsWholeBatch.cs` |
| `sqs.sqsbatch-send-full-width-one-invalid-entry-rejected` | `4df2823` | ported | `Sqs/SqsbatchSendFullWidthOneInvalidEntryRejected.cs` |
| `sqs.sqsbatch-send-partial-failure-preserves-survivor-attributes` | `4df2823` | ported | `Sqs/SqsbatchSendPartialFailurePreservesSurvivorAttributes.cs` |
| `sqs.sqsbatch-send-request-level-error-preempts-entry-failures-rejected` | `4df2823` | ported | `Sqs/SqsbatchSendRequestLevelErrorPreemptsEntryFailuresRejected.cs` |
| `sqs.sse-kms-consumer-without-decrypt-drives-message-to-dlq` | `4df2823` | not ported | KMS keys |
| `sqs.sse-kms-invalid-key-rejected` | `4df2823` | ported | `Sqs/SseKmsInvalidKeyRejected.cs` |
| `sqs.sse-kms-producer-needs-generate-data-key-on-a-customer-key` | `4df2823` | not ported | KMS keys |
| `sqs.sse-rejects-anonymous-sendmessage-that-an-unencrypted-queue-accepts` | `4df2823` | ported | `Sqs/SseRejectsAnonymousSendmessageThatAnUnencryptedQueueAccepts.cs` |
| `sqs.sse-sqs-is-the-default-and-exclusive-with-sse-kms` | `4df2823` | not ported | KMS keys |
| `sqs.sse-sqs-managed-encryption-round-trips-body-byte-exact` | `4df2823` | ported | `Sqs/SseSqsManagedEncryptionRoundTripsBodyByteExact.cs` |
| `sqs.standard-queue-concurrent-producers-and-consumers-lose-no-message` | `4df2823` | ported | `Sqs/StandardQueueConcurrentProducersAndConsumersLoseNoMessage.cs` |
| `sqs.standard-queue-holds-more-than-one-thousand-messages` | `4df2823` | ported | `Sqs/StandardQueueHoldsMoreThanOneThousandMessages.cs` |
| `sqs.standard-queue-message-deduplication-id-rejected` | `4df2823` | ported | `Sqs/StandardQueueMessageDeduplicationIdRejected.cs` |
| `sqs.standard-queue-message-group-id-is-surfaced-and-never-locks-the-group` | `4df2823` | ported | `Sqs/StandardQueueMessageGroupIdIsSurfacedAndNeverLocksTheGroup.cs` |
| `sqs.standard-queue-message-group-id-validated` | `4df2823` | ported | `Sqs/StandardQueueMessageGroupIdValidated.cs` |
| `sqs.start-message-move-task-destination-arn-to-third-queue-bypasses-original-source` | `4df2823` | ported | `Sqs/StartMessageMoveTaskDestinationArnToThirdQueueBypassesOriginalSource.cs` |
| `sqs.start-message-move-task-invalid-rate-rejected` | `4df2823` | ported | `Sqs/StartMessageMoveTaskInvalidRateRejected.cs` |
| `sqs.start-message-move-task-on-directly-sent-message-fails-could-not-determine-source` | `4df2823` | ported | `Sqs/StartMessageMoveTaskOnDirectlySentMessageFailsCouldNotDetermineSource.cs` |
| `sqs.start-message-move-task-self-referential-destination-rejected` | `4df2823` | ported | `Sqs/StartMessageMoveTaskSelfReferentialDestinationRejected.cs` |
| `sqs.start-message-move-task-type-mismatch-rejected` | `4df2823` | ported | `Sqs/StartMessageMoveTaskTypeMismatchRejected.cs` |
| `sqs.start-message-move-task-while-one-running-rejected` | `4df2823` | ported | `Sqs/StartMessageMoveTaskWhileOneRunningRejected.cs` |
| `sqs.tag-nonexistent-queue` | `4df2823` | ported | `Sqs/TagNonexistentQueue.cs` |
| `sqs.tag-operations-on-a-deleted-queue-rejected` | `4df2823` | ported | `Sqs/TagOperationsOnADeletedQueueRejected.cs` |
| `sqs.tag-queue-invalid-tag-key-rejected` | `4df2823` | ported | `Sqs/TagQueueInvalidTagKeyRejected.cs` |
| `sqs.tag-queue-merge-preserves-existing-tags` | `4df2823` | ported | `Sqs/TagQueueMergePreservesExistingTags.cs` |
| `sqs.tag-queue-over-fifty-tags-rejected` | `4df2823` | ported | `Sqs/TagQueueOverFiftyTagsRejected.cs` |
| `sqs.tag-queue-roundtrip` | `4df2823` | ported | `Sqs/TagQueueRoundtrip.cs` |
| `sqs.tag-queue-system-prefix-and-length-boundaries-rejected` | `4df2823` | ported | `Sqs/TagQueueSystemPrefixAndLengthBoundariesRejected.cs` |
| `sqs.visibility-extension-capped-at-twelve-hours-from-receive-rejected` | `4df2823` | ported | `Sqs/VisibilityExtensionCappedAtTwelveHoursFromReceiveRejected.cs` |
| `sqs.visibility-heartbeat-hides-a-message-from-concurrent-pollers` | `4df2823` | ported | `Sqs/VisibilityHeartbeatHidesAMessageFromConcurrentPollers.cs` |
| `sqs.visibility-timeout-natural-expiry` | `4df2823` | ported | `Sqs/VisibilityTimeoutNaturalExpiry.cs` |
| `sqs.visibility-timeout-out-of-range-rejected` | `4df2823` | ported | `Sqs/VisibilityTimeoutOutOfRangeRejected.cs` |
| `sqs.visibility-timeout-shorten-and-receive-override` | `4df2823` | ported | `Sqs/VisibilityTimeoutShortenAndReceiveOverride.cs` |

## behavioral/s3

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `s3.50-bucket-fanout-object-isolation-and-negative-space` | `4df2823` | ported | `S3/Check50BucketFanoutObjectIsolationAndNegativeSpace.cs` |
| `s3.abac-policy-enforcement` | `4df2823` | not ported | IAM roles and role assumption |
| `s3.abort-multipart-nosuchupload-after-complete` | `4df2823` | ported | `S3/AbortMultipartNosuchuploadAfterComplete.cs` |
| `s3.acl-disabled-bucket-rejects-put-object-acl` | `4df2823` | ported | `S3/AclDisabledBucketRejectsPutObjectAcl.cs` |
| `s3.annotation-name-validation-rejected` | `4df2823` | ported | `S3/AnnotationNameValidationRejected.cs` |
| `s3.bucket-abac-invalid-status-rejected` | `4df2823` | ported | `S3/BucketAbacInvalidStatusRejected.cs` |
| `s3.bucket-abac-roundtrip` | `4df2823` | ported | `S3/BucketAbacRoundtrip.cs` |
| `s3.bucket-accelerate-configuration-roundtrip` | `4df2823` | ported | `S3/BucketAccelerateConfigurationRoundtrip.cs` |
| `s3.bucket-acl-conflicting-grants-rejected` | `4df2823` | ported | `S3/BucketAclConflictingGrantsRejected.cs` |
| `s3.bucket-acl-owner-full-control-default` | `4df2823` | ported | `S3/BucketAclOwnerFullControlDefault.cs` |
| `s3.bucket-acl-public-grant-blocked-by-public-access-block-rejected` | `4df2823` | ported | `S3/BucketAclPublicGrantBlockedByPublicAccessBlockRejected.cs` |
| `s3.bucket-acl-put-get-roundtrip` | `4df2823` | ported | `S3/BucketAclPutGetRoundtrip.cs` |
| `s3.bucket-acl-roundtrip` | `4df2823` | ported | `S3/BucketAclRoundtrip.cs` |
| `s3.bucket-acl-write-blocked-by-ownership-enforced` | `4df2823` | ported | `S3/BucketAclWriteBlockedByOwnershipEnforced.cs` |
| `s3.bucket-analytics-configuration-lifecycle` | `4df2823` | ported | `S3/BucketAnalyticsConfigurationLifecycle.cs` |
| `s3.bucket-analytics-configuration-roundtrip` | `4df2823` | ported | `S3/BucketAnalyticsConfigurationRoundtrip.cs` |
| `s3.bucket-create-delete` | `4df2823` | ported | `S3/BucketCreateDelete.cs` |
| `s3.bucket-default-retention-derives-the-retain-until-date-and-blocks-the-delete` | `4df2823` | ported | `S3/BucketDefaultRetentionDerivesTheRetainUntilDateAndBlocksTheDelete.cs` |
| `s3.bucket-encryption-config-lifecycle` | `4df2823` | ported | `S3/BucketEncryptionConfigLifecycle.cs` |
| `s3.bucket-encryption-lifecycle` | `4df2823` | ported | `S3/BucketEncryptionLifecycle.cs` |
| `s3.bucket-holding-more-than-sixty-four-mib-reads-back-every-object` | `4df2823` | ported | `S3/BucketHoldingMoreThanSixtyFourMibReadsBackEveryObject.cs` |
| `s3.bucket-intelligent-tiering-archive-tier-minimum-days-rejected` | `4df2823` | ported | `S3/BucketIntelligentTieringArchiveTierMinimumDaysRejected.cs` |
| `s3.bucket-intelligent-tiering-configuration-lifecycle` | `4df2823` | ported | `S3/BucketIntelligentTieringConfigurationLifecycle.cs` |
| `s3.bucket-intelligent-tiering-filtered-configs-replace-not-duplicate` | `4df2823` | ported | `S3/BucketIntelligentTieringFilteredConfigsReplaceNotDuplicate.cs` |
| `s3.bucket-intelligent-tiering-roundtrip` | `4df2823` | ported | `S3/BucketIntelligentTieringRoundtrip.cs` |
| `s3.bucket-inventory-configuration-lifecycle` | `4df2823` | ported | `S3/BucketInventoryConfigurationLifecycle.cs` |
| `s3.bucket-inventory-configuration-roundtrip` | `4df2823` | ported | `S3/BucketInventoryConfigurationRoundtrip.cs` |
| `s3.bucket-logging-config-roundtrip` | `4df2823` | ported | `S3/BucketLoggingConfigRoundtrip.cs` |
| `s3.bucket-logging-roundtrip` | `4df2823` | ported | `S3/BucketLoggingRoundtrip.cs` |
| `s3.bucket-metadata-annotation-table-update` | `4df2823` | ported | `S3/BucketMetadataAnnotationTableUpdate.cs` |
| `s3.bucket-metadata-configuration-duplicate-create-rejected` | `4df2823` | ported | `S3/BucketMetadataConfigurationDuplicateCreateRejected.cs` |
| `s3.bucket-metadata-configuration-lifecycle` | `4df2823` | ported | `S3/BucketMetadataConfigurationLifecycle.cs` |
| `s3.bucket-metadata-configuration-roundtrip` | `4df2823` | ported | `S3/BucketMetadataConfigurationRoundtrip.cs` |
| `s3.bucket-metadata-inventory-table-update` | `4df2823` | ported | `S3/BucketMetadataInventoryTableUpdate.cs` |
| `s3.bucket-metadata-journal-table-update` | `4df2823` | ported | `S3/BucketMetadataJournalTableUpdate.cs` |
| `s3.bucket-metadata-table-configuration-lifecycle` | `4df2823` | ported | `S3/BucketMetadataTableConfigurationLifecycle.cs` |
| `s3.bucket-metadata-table-update-roundtrip` | `4df2823` | ported | `S3/BucketMetadataTableUpdateRoundtrip.cs` |
| `s3.bucket-metadata-table-updates-refused-while-journal-creating` | `4df2823` | ported | `S3/BucketMetadataTableUpdatesRefusedWhileJournalCreating.cs` |
| `s3.bucket-metrics-configuration-delete-reflected-in-list-then-get-rejected` | `4df2823` | ported | `S3/BucketMetricsConfigurationDeleteReflectedInListThenGetRejected.cs` |
| `s3.bucket-metrics-configuration-filter-prefix-and-tag-combo` | `4df2823` | ported | `S3/BucketMetricsConfigurationFilterPrefixAndTagCombo.cs` |
| `s3.bucket-metrics-configuration-multi-filter-listing` | `4df2823` | ported | `S3/BucketMetricsConfigurationMultiFilterListing.cs` |
| `s3.bucket-ownership-controls-invalid-value-rejected` | `4df2823` | ported | `S3/BucketOwnershipControlsInvalidValueRejected.cs` |
| `s3.bucket-ownership-controls-lifecycle` | `4df2823` | ported | `S3/BucketOwnershipControlsLifecycle.cs` |
| `s3.bucket-ownership-controls-roundtrip` | `4df2823` | ported | `S3/BucketOwnershipControlsRoundtrip.cs` |
| `s3.bucket-policy-deny-enforcement` | `4df2823` | ported | `S3/BucketPolicyDenyEnforcement.cs` |
| `s3.bucket-policy-roundtrip` | `4df2823` | ported | `S3/BucketPolicyRoundtrip.cs` |
| `s3.bucket-policy-status-reflects-policy` | `4df2823` | ported | `S3/BucketPolicyStatusReflectsPolicy.cs` |
| `s3.bucket-replication-config-lifecycle` | `4df2823` | ported | `S3/BucketReplicationConfigLifecycle.cs` |
| `s3.bucket-replication-configuration-lifecycle` | `4df2823` | ported | `S3/BucketReplicationConfigurationLifecycle.cs` |
| `s3.bucket-replication-destination-validation-rejected` | `4df2823` | ported | `S3/BucketReplicationDestinationValidationRejected.cs` |
| `s3.bucket-replication-lifecycle` | `4df2823` | ported | `S3/BucketReplicationLifecycle.cs` |
| `s3.bucket-replication-requires-versioning-rejected` | `4df2823` | ported | `S3/BucketReplicationRequiresVersioningRejected.cs` |
| `s3.bucket-replication-roundtrip` | `4df2823` | ported | `S3/BucketReplicationRoundtrip.cs` |
| `s3.bucket-request-payment-roundtrip` | `4df2823` | ported | `S3/BucketRequestPaymentRoundtrip.cs` |
| `s3.bucket-subresource-deletes-leave-bucket-and-objects-intact` | `4df2823` | ported | `S3/BucketSubresourceDeletesLeaveBucketAndObjectsIntact.cs` |
| `s3.bucket-tagging-roundtrip` | `4df2823` | ported | `S3/BucketTaggingRoundtrip.cs` |
| `s3.bucket-website-config-lifecycle` | `4df2823` | ported | `S3/BucketWebsiteConfigLifecycle.cs` |
| `s3.bucket-website-configuration-lifecycle` | `4df2823` | ported | `S3/BucketWebsiteConfigurationLifecycle.cs` |
| `s3.bucket-website-roundtrip` | `4df2823` | ported | `S3/BucketWebsiteRoundtrip.cs` |
| `s3.checksum-crc32` | `4df2823` | ported | `S3/ChecksumCrc32.cs` |
| `s3.checksum-sha256` | `4df2823` | ported | `S3/ChecksumSha256.cs` |
| `s3.cli-create-sdk-read-s3` | `4df2823` | ported | `S3/CliCreateSdkReadS3.cs` |
| `s3.cli-s3-cp-large-file-multipart-checksum-defaults` | `4df2823` | ported | `S3/CliS3CpLargeFileMultipartChecksumDefaults.cs` |
| `s3.cli-s3-cp-large-file-multipart-etag-shape` | `4df2823` | ported | `S3/CliS3CpLargeFileMultipartEtagShape.cs` |
| `s3.cli-s3-get-nonexistent-key-error` | `4df2823` | ported | `S3/CliS3GetNonexistentKeyError.cs` |
| `s3.cli-s3-put-get-roundtrip` | `4df2823` | ported | `S3/CliS3PutGetRoundtrip.cs` |
| `s3.cli-script-s3-sync-workflow` | `4df2823` | ported | `S3/CliScriptS3SyncWorkflow.cs` |
| `s3.conc-s3-conditional-writes-complete-multipart-if-none-match-rejected-when-key-appears-mid-upload` | `4df2823` | ported | `S3/ConcS3ConditionalWritesCompleteMultipartIfNoneMatchRejectedWhenKeyAppearsMidUpload.cs` |
| `s3.conc-s3-conditional-writes-complete-multipart-stale-if-match-rejected` | `4df2823` | ported | `S3/ConcS3ConditionalWritesCompleteMultipartStaleIfMatchRejected.cs` |
| `s3.conc-s3-conditional-writes-concurrent-multipart-completions-exactly-one-wins` | `4df2823` | ported | `S3/ConcS3ConditionalWritesConcurrentMultipartCompletionsExactlyOneWins.cs` |
| `s3.conc-s3-conditional-writes-get-during-overwrite-returns-a-whole-version` | `4df2823` | ported | `S3/ConcS3ConditionalWritesGetDuringOverwriteReturnsAWholeVersion.cs` |
| `s3.conc-s3-conditional-writes-if-match-on-absent-key-rejected-and-nothing-created` | `4df2823` | ported | `S3/ConcS3ConditionalWritesIfMatchOnAbsentKeyRejectedAndNothingCreated.cs` |
| `s3.conc-s3-conditional-writes-if-match-over-delete-marker-rejected` | `4df2823` | ported | `S3/ConcS3ConditionalWritesIfMatchOverDeleteMarkerRejected.cs` |
| `s3.conc-s3-conditional-writes-lock-stale-holder-release-rejected` | `4df2823` | ported | `S3/ConcS3ConditionalWritesLockStaleHolderReleaseRejected.cs` |
| `s3.conc-s3-conditional-writes-optimistic-counter-loses-no-increment` | `4df2823` | ported | `S3/ConcS3ConditionalWritesOptimisticCounterLosesNoIncrement.cs` |
| `s3.conc-s3-conditional-writes-read-after-conditional-overwrite-every-read-path-is-current` | `4df2823` | ported | `S3/ConcS3ConditionalWritesReadAfterConditionalOverwriteEveryReadPathIsCurrent.cs` |
| `s3.conc-s3-conditional-writes-unconditional-concurrent-puts-leave-exactly-one-whole-body` | `4df2823` | ported | `S3/ConcS3ConditionalWritesUnconditionalConcurrentPutsLeaveExactlyOneWholeBody.cs` |
| `s3.conc-s3-conditional-writes-versioned-noncurrent-etag-if-match-rejected` | `4df2823` | ported | `S3/ConcS3ConditionalWritesVersionedNoncurrentEtagIfMatchRejected.cs` |
| `s3.concurrent-conditional-create-exactly-one-wins` | `4df2823` | ported | `S3/ConcurrentConditionalCreateExactlyOneWins.cs` |
| `s3.concurrent-multipart-completion-race` | `4df2823` | ported | `S3/ConcurrentMultipartCompletionRace.cs` |
| `s3.concurrent-put-if-none-match-exactly-one-wins` | `4df2823` | ported | `S3/ConcurrentPutIfNoneMatchExactlyOneWins.cs` |
| `s3.concurrent-versioned-puts-no-lost-writes` | `4df2823` | ported | `S3/ConcurrentVersionedPutsNoLostWrites.cs` |
| `s3.content-encoding-preserved` | `4df2823` | ported | `S3/ContentEncodingPreserved.cs` |
| `s3.content-type-preserved` | `4df2823` | ported | `S3/ContentTypePreserved.cs` |
| `s3.copy-object-and-ranged-get-return-exact-source-bytes-then-batch-delete-removes-both` | `4df2823` | ported | `S3/CopyObjectAndRangedGetReturnExactSourceBytesThenBatchDeleteRemovesBoth.cs` |
| `s3.copy-object-annotation-exclude-directive-skips-default-copies` | `4df2823` | ported | `S3/CopyObjectAnnotationExcludeDirectiveSkipsDefaultCopies.cs` |
| `s3.copy-object-cross-bucket` | `4df2823` | ported | `S3/CopyObjectCrossBucket.cs` |
| `s3.copy-object-from-specific-version-source` | `4df2823` | ported | `S3/CopyObjectFromSpecificVersionSource.cs` |
| `s3.copy-object-glacier-not-in-active-tier-error` | `4df2823` | ported | `S3/CopyObjectGlacierNotInActiveTierError.cs` |
| `s3.copy-object-glacier-source-error` | `4df2823` | ported | `S3/CopyObjectGlacierSourceError.cs` |
| `s3.copy-object-if-match-on-the-destination-refuses-a-stale-etag` | `4df2823` | ported | `S3/CopyObjectIfMatchOnTheDestinationRefusesAStaleEtag.cs` |
| `s3.copy-object-into-directory-bucket-rejects-source-tags-and-destination-ssec` | `4df2823` | ported | `S3/CopyObjectIntoDirectoryBucketRejectsSourceTagsAndDestinationSsec.cs` |
| `s3.copy-object-metadata-copy` | `4df2823` | ported | `S3/CopyObjectMetadataCopy.cs` |
| `s3.copy-object-metadata-replace` | `4df2823` | ported | `S3/CopyObjectMetadataReplace.cs` |
| `s3.copy-object-missing-source-permission-denied` | `4df2823` | ported | `S3/CopyObjectMissingSourcePermissionDenied.cs` |
| `s3.copy-object-not-in-active-tier-rejected` | `4df2823` | ported | `S3/CopyObjectNotInActiveTierRejected.cs` |
| `s3.copy-object-same-bucket` | `4df2823` | ported | `S3/CopyObjectSameBucket.cs` |
| `s3.copy-object-source-if-match-mismatch-rejected` | `4df2823` | ported | `S3/CopyObjectSourceIfMatchMismatchRejected.cs` |
| `s3.copy-object-to-itself-no-change-rejected` | `4df2823` | ported | `S3/CopyObjectToItselfNoChangeRejected.cs` |
| `s3.copy-object-under-if-none-match-policy` | `4df2823` | ported | `S3/CopyObjectUnderIfNoneMatchPolicy.cs` |
| `s3.cors-configuration-roundtrip` | `4df2823` | ported | `S3/CorsConfigurationRoundtrip.cs` |
| `s3.cors-delete` | `4df2823` | ported | `S3/CorsDelete.cs` |
| `s3.cors-preflight-allowed-origin-returns-rule-headers` | `4df2823` | ported | `S3/CorsPreflightAllowedOriginReturnsRuleHeaders.cs` |
| `s3.cors-preflight-disallowed-method-rejected-allowed-method-passes` | `4df2823` | ported | `S3/CorsPreflightDisallowedMethodRejectedAllowedMethodPasses.cs` |
| `s3.cors-preflight-disallowed-origin-rejected-allowed-origin-passes` | `4df2823` | ported | `S3/CorsPreflightDisallowedOriginRejectedAllowedOriginPasses.cs` |
| `s3.cors-preflight-on-bucket-without-cors-configuration-rejected` | `4df2823` | ported | `S3/CorsPreflightOnBucketWithoutCorsConfigurationRejected.cs` |
| `s3.cors-preflight-refused-after-delete-bucket-cors` | `4df2823` | ported | `S3/CorsPreflightRefusedAfterDeleteBucketCors.cs` |
| `s3.cors-presigned-put-and-get-with-origin-carry-allow-origin` | `4df2823` | ported | `S3/CorsPresignedPutAndGetWithOriginCarryAllowOrigin.cs` |
| `s3.cors-presigned-with-origin-without-cors-config-no-cors-headers` | `4df2823` | ported | `S3/CorsPresignedWithOriginWithoutCorsConfigNoCorsHeaders.cs` |
| `s3.create-bucket-invalid-name-rejected` | `4df2823` | ported | `S3/CreateBucketInvalidNameRejected.cs` |
| `s3.create-bucket-invalid-name-variants-rejected` | `4df2823` | ported | `S3/CreateBucketInvalidNameVariantsRejected.cs` |
| `s3.create-bucket-metadata-table-configuration-name-conflict-rejected` | `4df2823` | ported | `S3/CreateBucketMetadataTableConfigurationNameConflictRejected.cs` |
| `s3.create-bucket-reserved-prefix-or-suffix-rejected` | `4df2823` | ported | `S3/CreateBucketReservedPrefixOrSuffixRejected.cs` |
| `s3.create-session-express-zone` | `4df2823` | ported | `S3/CreateSessionExpressZone.cs` |
| `s3.create-session-no-such-bucket` | `4df2823` | ported | `S3/CreateSessionNoSuchBucket.cs` |
| `s3.create-session-nosuchbucket` | `4df2823` | ported | `S3/CreateSessionNosuchbucketGeneralPurposeBucket.cs` |
| `s3.create-session-roundtrip` | `4df2823` | ported | `S3/CreateSessionRoundtrip.cs` |
| `s3.create-session-token-ttl` | `4df2823` | ported | `S3/CreateSessionTokenTtl.cs` |
| `s3.deep-s3-immutability-compliance-lock-extends-and-overwrites-but-weakening-rejected` | `4df2823` | ported | `S3/DeepS3ImmutabilityComplianceLockExtendsAndOverwritesButWeakeningRejected.cs` |
| `s3.deep-s3-immutability-composite-sha256-checksum-reports-parts` | `4df2823` | ported | `S3/DeepS3ImmutabilityCompositeSha256ChecksumReportsParts.cs` |
| `s3.deep-s3-immutability-copy-metadata-directive-replace-drops-unrestated` | `4df2823` | ported | `S3/DeepS3ImmutabilityCopyMetadataDirectiveReplaceDropsUnrestated.cs` |
| `s3.deep-s3-immutability-copy-tagging-directive-independent-of-metadata` | `4df2823` | ported | `S3/DeepS3ImmutabilityCopyTaggingDirectiveIndependentOfMetadata.cs` |
| `s3.deep-s3-immutability-copy-unknown-tagging-directive-rejected` | `4df2823` | ported | `S3/DeepS3ImmutabilityCopyUnknownTaggingDirectiveRejected.cs` |
| `s3.deep-s3-immutability-governance-bypass-without-permission-denied` | `4df2823` | ported | `S3/DeepS3ImmutabilityGovernanceBypassWithoutPermissionDenied.cs` |
| `s3.deep-s3-immutability-legal-hold-blocks-bypass-independent-of-retention-rejected` | `4df2823` | ported | `S3/DeepS3ImmutabilityLegalHoldBlocksBypassIndependentOfRetentionRejected.cs` |
| `s3.deep-s3-immutability-multipart-etag-is-md5-of-part-md5s` | `4df2823` | ported | `S3/DeepS3ImmutabilityMultipartEtagIsMd5OfPartMd5s.cs` |
| `s3.deep-s3-immutability-requester-pays-anonymous-with-payer-header-rejected` | `4df2823` | ported | `S3/DeepS3ImmutabilityRequesterPaysAnonymousWithPayerHeaderRejected.cs` |
| `s3.deep-s3-immutability-upload-part-wrong-sha256-rejected-slot-stays-empty` | `4df2823` | ported | `S3/DeepS3ImmutabilityUploadPartWrongSha256RejectedSlotStaysEmpty.cs` |
| `s3.deep-s3-storage-class-copy-in-place-to-deep-archive-closes-reads` | `4df2823` | ported | `S3/DeepS3StorageClassCopyInPlaceToDeepArchiveClosesReads.cs` |
| `s3.deep-s3-storage-class-deep-archive-expedited-restore-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassDeepArchiveExpeditedRestoreRejected.cs` |
| `s3.deep-s3-storage-class-deep-archive-get-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassDeepArchiveGetRejected.cs` |
| `s3.deep-s3-storage-class-express-onezone-class-put-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassExpressOnezoneClassPutRejected.cs` |
| `s3.deep-s3-storage-class-glacier-ir-restore-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassGlacierIrRestoreRejected.cs` |
| `s3.deep-s3-storage-class-lifecycle-transition-colder-before-warmer-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassLifecycleTransitionColderBeforeWarmerRejected.cs` |
| `s3.deep-s3-storage-class-lifecycle-transition-glacier-to-deep-archive-gap-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassLifecycleTransitionGlacierToDeepArchiveGapRejected.cs` |
| `s3.deep-s3-storage-class-lifecycle-transition-ia-to-glacier-gap-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassLifecycleTransitionIaToGlacierGapRejected.cs` |
| `s3.deep-s3-storage-class-lifecycle-transition-invalid-target-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassLifecycleTransitionInvalidTargetRejected.cs` |
| `s3.deep-s3-storage-class-multipart-class-complete-after-abort-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassMultipartClassCompleteAfterAbortRejected.cs` |
| `s3.deep-s3-storage-class-noncurrent-expiration-zero-days-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassNoncurrentExpirationZeroDaysRejected.cs` |
| `s3.deep-s3-storage-class-onezone-ia-intelligent-tiering-direct-put` | `4df2823` | ported | `S3/DeepS3StorageClassOnezoneIaIntelligentTieringDirectPut.cs` |
| `s3.deep-s3-storage-class-outposts-class-put-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassOutpostsClassPutRejected.cs` |
| `s3.deep-s3-storage-class-restore-tier-downgrade-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassRestoreTierDowngradeRejected.cs` |
| `s3.deep-s3-storage-class-restore-zero-days-rejected` | `4df2823` | ported | `S3/DeepS3StorageClassRestoreZeroDaysRejected.cs` |
| `s3.deep-s3-storage-class-versioned-expiration-rule-targets-current-version` | `4df2823` | ported | `S3/DeepS3StorageClassVersionedExpirationRuleTargetsCurrentVersion.cs` |
| `s3.delete-bucket-holding-only-a-delete-marker-rejected-bucket-not-empty` | `4df2823` | ported | `S3/DeleteBucketHoldingOnlyADeleteMarkerRejectedBucketNotEmpty.cs` |
| `s3.delete-bucket-with-metadata-table-configuration-blocked` | `4df2823` | ported | `S3/DeleteBucketWithMetadataTableConfigurationBlocked.cs` |
| `s3.delete-nonempty-bucket-rejected-across-20-bucket-fanout` | `4df2823` | ported | `S3/DeleteNonemptyBucketRejectedAcross20BucketFanout.cs` |
| `s3.delete-object-annotation-error-paths` | `4df2823` | ported | `S3/DeleteObjectAnnotationErrorPaths.cs` |
| `s3.delete-object-annotation-missing-rejected` | `4df2823` | ported | `S3/DeleteObjectAnnotationMissingRejected.cs` |
| `s3.delete-object-nonexistent-idempotent` | `4df2823` | ported | `S3/DeleteObjectNonexistentIdempotent.cs` |
| `s3.delete-object-policy-requires-if-match-header` | `4df2823` | ported | `S3/DeleteObjectPolicyRequiresIfMatchHeader.cs` |
| `s3.delete-object-under-legal-hold-rejected` | `4df2823` | ported | `S3/DeleteObjectUnderLegalHoldRejected.cs` |
| `s3.delete-object-under-retention-rejected` | `4df2823` | ported | `S3/DeleteObjectUnderRetentionRejected.cs` |
| `s3.delete-objects-conditional-etag-mismatch-survives` | `4df2823` | ported | `S3/DeleteObjectsConditionalEtagMismatchSurvives.cs` |
| `s3.delete-objects-empty-list-rejected` | `4df2823` | ported | `S3/DeleteObjectsEmptyListRejected.cs` |
| `s3.delete-objects-exceeds-max-keys-rejected` | `4df2823` | ported | `S3/DeleteObjectsExceedsMaxKeysRejected.cs` |
| `s3.delete-objects-partial-failure` | `4df2823` | ported | `S3/DeleteObjectsPartialFailure.cs` |
| `s3.delete-objects-quiet-mode` | `4df2823` | ported | `S3/DeleteObjectsQuietMode.cs` |
| `s3.delete-objects-that-misses-one-key-leaves-delete-bucket-refused` | `4df2823` | ported | `S3/DeleteObjectsThatMissesOneKeyLeavesDeleteBucketRefused.cs` |
| `s3.deleting-the-only-object-under-a-nested-prefix-removes-its-implied-directories` | `4df2823` | ported | `S3/DeletingTheOnlyObjectUnderANestedPrefixRemovesItsImpliedDirectories.cs` |
| `s3.deploy-s3-1-bucket-update-versioning-policy-rename` | `4df2823` | not ported | CloudFormation stacks |
| `s3.deploy-s3-1-rename-onto-owned-bucket-rejected` | `4df2823` | not ported | CloudFormation stacks |
| `s3.deploy-s3-bucket-default-encryption-applied` | `4df2823` | not ported | CloudFormation stacks |
| `s3.deploy-s3-bucket-public-access-block-enforced-rejected` | `4df2823` | not ported | CloudFormation stacks |
| `s3.deploy-s3-bucket-versioning-behavior` | `4df2823` | not ported | CloudFormation stacks |
| `s3.directory-bucket-multipart-complete-etag-is-not-an-md5-composite` | `4df2823` | ported | `S3/DirectoryBucketMultipartCompleteEtagIsNotAnMd5Composite.cs` |
| `s3.directory-bucket-rejects-object-tagging-versioning-notification-and-replication` | `4df2823` | ported | `S3/DirectoryBucketRejectsObjectTaggingVersioningNotificationAndReplication.cs` |
| `s3.directory-buckets-listing` | `4df2823` | ported | `S3/DirectoryBucketsListing.cs` |
| `s3.e2e-log-rotation-pattern` | `4df2823` | ported | `S3/E2eLogRotationPattern.cs` |
| `s3.e2e-website-deployment` | `4df2823` | ported | `S3/E2eWebsiteDeployment.cs` |
| `s3.empty-and-odd-sized-objects-under-unicode-keys-round-trip-with-md5-etags` | `4df2823` | ported | `S3/EmptyAndOddSizedObjectsUnderUnicodeKeysRoundTripWithMd5Etags.cs` |
| `s3.encryption-enforcement-reject-unencrypted` | `4df2823` | ported | `S3/EncryptionEnforcementRejectUnencrypted.cs` |
| `s3.error-bucket-already-owned` | `4df2823` | ported | `S3/ErrorBucketAlreadyOwned.cs` |
| `s3.error-bucket-not-empty` | `4df2823` | ported | `S3/ErrorBucketNotEmpty.cs` |
| `s3.error-invalid-bucket-name` | `4df2823` | ported | `S3/ErrorInvalidBucketName.cs` |
| `s3.error-no-such-bucket` | `4df2823` | ported | `S3/ErrorNoSuchBucket.cs` |
| `s3.error-no-such-key` | `4df2823` | ported | `S3/ErrorNoSuchKey.cs` |
| `s3.etag-is-md5-for-single-put` | `4df2823` | ported | `S3/EtagIsMd5ForSinglePut.cs` |
| `s3.expected-bucket-owner-mismatch-rejected` | `4df2823` | ported | `S3/ExpectedBucketOwnerMismatchRejected.cs` |
| `s3.get-bucket-replication-not-configured-rejected` | `4df2823` | ported | `S3/GetBucketReplicationNotConfiguredRejected.cs` |
| `s3.get-bucket-versioning-unset-fresh` | `4df2823` | ported | `S3/GetBucketVersioningUnsetFresh.cs` |
| `s3.get-bucket-website-not-configured-rejected` | `4df2823` | ported | `S3/GetBucketWebsiteNotConfiguredRejected.cs` |
| `s3.get-glacier-object-without-restore-rejected` | `4df2823` | ported | `S3/GetGlacierObjectWithoutRestoreRejected.cs` |
| `s3.get-object-acl-no-such-key` | `4df2823` | ported | `S3/GetObjectAclNoSuchKey.cs` |
| `s3.get-object-annotation-nosuchannotation` | `4df2823` | ported | `S3/GetObjectAnnotationNosuchannotation.cs` |
| `s3.get-object-attributes-invalid-attribute-name-rejected` | `4df2823` | ported | `S3/GetObjectAttributesInvalidAttributeNameRejected.cs` |
| `s3.get-object-attributes-no-such-key` | `4df2823` | ported | `S3/GetObjectAttributesNoSuchKey.cs` |
| `s3.get-object-attributes-nosuchkey` | `4df2823` | ported | `S3/GetObjectAttributesNosuchkeyCompact.cs` |
| `s3.get-object-attributes-size-etag` | `4df2823` | ported | `S3/GetObjectAttributesSizeEtag.cs` |
| `s3.get-object-invalid-object-state-glacier` | `4df2823` | ported | `S3/GetObjectInvalidObjectStateGlacier.cs` |
| `s3.get-object-invalid-state-archived` | `4df2823` | ported | `S3/GetObjectInvalidStateArchived.cs` |
| `s3.get-object-nonexistent-version-errors` | `4df2823` | ported | `S3/GetObjectNonexistentVersionErrors.cs` |
| `s3.get-object-part-number-reads-single-part` | `4df2823` | ported | `S3/GetObjectPartNumberReadsSinglePart.cs` |
| `s3.get-object-torrent` | `4df2823` | ported | `S3/GetObjectTorrent.cs` |
| `s3.get-object-torrent-unsupported-rejected` | `4df2823` | ported | `S3/GetObjectTorrentUnsupportedRejected.cs` |
| `s3.head-object-metadata` | `4df2823` | ported | `S3/HeadObjectMetadata.cs` |
| `s3.head-object-on-delete-marker-rejected` | `4df2823` | ported | `S3/HeadObjectOnDeleteMarkerRejected.cs` |
| `s3.identity-policy-s3-prefix-condition-scopes-listbucket-and-getobject-to-a-home-prefix` | `4df2823` | not ported | IAM roles and role assumption |
| `s3.if-match-failure` | `4df2823` | ported | `S3/IfMatchFailure.cs` |
| `s3.if-match-success` | `4df2823` | ported | `S3/IfMatchSuccess.cs` |
| `s3.if-modified-since` | `4df2823` | ported | `S3/IfModifiedSince.cs` |
| `s3.if-none-match` | `4df2823` | ported | `S3/IfNoneMatch.cs` |
| `s3.lifecycle-abort-incomplete-multipart` | `4df2823` | ported | `S3/LifecycleAbortIncompleteMultipart.cs` |
| `s3.lifecycle-abort-incomplete-multipart-invalid-days-rejected` | `4df2823` | ported | `S3/LifecycleAbortIncompleteMultipartInvalidDaysRejected.cs` |
| `s3.lifecycle-expiration-ttl` | `4df2823` | ported | `S3/LifecycleExpirationTtl.cs` |
| `s3.lifecycle-rule-delete` | `4df2823` | ported | `S3/LifecycleRuleDelete.cs` |
| `s3.lifecycle-rule-roundtrip` | `4df2823` | ported | `S3/LifecycleRuleRoundtrip.cs` |
| `s3.lifecycle-transition-server-side` | `4df2823` | ported | `S3/LifecycleTransitionServerSide.cs` |
| `s3.list-directory-buckets-isolation` | `4df2823` | ported | `S3/ListDirectoryBucketsIsolation.cs` |
| `s3.list-directory-buckets-rejects-regular-bucket-ops` | `4df2823` | ported | `S3/ListDirectoryBucketsRejectsRegularBucketOps.cs` |
| `s3.list-multipart-uploads-paginates-and-groups-common-prefixes` | `4df2823` | ported | `S3/ListMultipartUploadsPaginatesAndGroupsCommonPrefixes.cs` |
| `s3.list-object-annotations-invalid-prefix` | `4df2823` | ported | `S3/ListObjectAnnotationsInvalidPrefix.cs` |
| `s3.list-object-versions` | `4df2823` | ported | `S3/ListObjectVersions.cs` |
| `s3.list-object-versions-pages-one-key-written-more-than-one-thousand-times` | `4df2823` | ported | `S3/ListObjectVersionsPagesOneKeyWrittenMoreThanOneThousandTimes.cs` |
| `s3.list-object-versions-pagination-markers` | `4df2823` | ported | `S3/ListObjectVersionsPaginationMarkers.cs` |
| `s3.list-objects-nosuchbucket` | `4df2823` | ported | `S3/ListObjectsNosuchbucket.cs` |
| `s3.list-objects-v1-basic-listing` | `4df2823` | ported | `S3/ListObjectsV1BasicListing.cs` |
| `s3.list-objects-v1-marker-pagination` | `4df2823` | ported | `S3/ListObjectsV1MarkerPagination.cs` |
| `s3.list-objects-v2-basic` | `4df2823` | ported | `S3/ListObjectsV2Basic.cs` |
| `s3.list-objects-v2-byte-order-and-delimiter-paging` | `4df2823` | ported | `S3/ListObjectsV2ByteOrderAndDelimiterPaging.cs` |
| `s3.list-objects-v2-default-page-holds-one-thousand-keys-of-a-larger-bucket` | `4df2823` | ported | `S3/ListObjectsV2DefaultPageHoldsOneThousandKeysOfALargerBucket.cs` |
| `s3.list-objects-v2-delimiter` | `4df2823` | ported | `S3/ListObjectsV2Delimiter.cs` |
| `s3.list-objects-v2-is-exact-on-the-first-read-after-a-bulk-delete` | `4df2823` | ported | `S3/ListObjectsV2IsExactOnTheFirstReadAfterABulkDelete.cs` |
| `s3.list-objects-v2-prefix` | `4df2823` | ported | `S3/ListObjectsV2Prefix.cs` |
| `s3.list-parts-max-parts-pagination-returns-each-part-once` | `4df2823` | ported | `S3/ListPartsMaxPartsPaginationReturnsEachPartOnce.cs` |
| `s3.metadata-table-configuration-create-active-status` | `4df2823` | ported | `S3/MetadataTableConfigurationCreateActiveStatus.cs` |
| `s3.metadata-table-configuration-delete-removes-table` | `4df2823` | ported | `S3/MetadataTableConfigurationDeleteRemovesTable.cs` |
| `s3.metadata-table-configuration-duplicate-create-rejected` | `4df2823` | ported | `S3/MetadataTableConfigurationDuplicateCreateRejected.cs` |
| `s3.metadata-table-configuration-get-without-create-rejected` | `4df2823` | ported | `S3/MetadataTableConfigurationGetWithoutCreateRejected.cs` |
| `s3.metadata-table-configuration-invalid-schema-rejected` | `4df2823` | ported | `S3/MetadataTableConfigurationInvalidSchemaRejected.cs` |
| `s3.metadata-table-server-managed-updates` | `4df2823` | ported | `S3/MetadataTableServerManagedUpdates.cs` |
| `s3.multipart-abort` | `4df2823` | ported | `S3/MultipartAbort.cs` |
| `s3.multipart-abort-no-such-upload-rejected` | `4df2823` | ported | `S3/MultipartAbortNoSuchUploadRejected.cs` |
| `s3.multipart-complete-invalid-part-etag-rejected` | `4df2823` | ported | `S3/MultipartCompleteInvalidPartEtagRejected.cs` |
| `s3.multipart-complete-out-of-order-rejected` | `4df2823` | ported | `S3/MultipartCompleteOutOfOrderRejected.cs` |
| `s3.multipart-complete-part-ordering-enforced` | `4df2823` | ported | `S3/MultipartCompletePartOrderingEnforced.cs` |
| `s3.multipart-complete-part-too-small-rejected` | `4df2823` | ported | `S3/MultipartCompletePartTooSmallRejected.cs` |
| `s3.multipart-etag-format` | `4df2823` | ported | `S3/MultipartEtagFormat.cs` |
| `s3.multipart-list-parts` | `4df2823` | ported | `S3/MultipartListParts.cs` |
| `s3.multipart-list-parts-after-abort-rejected` | `4df2823` | ported | `S3/MultipartListPartsAfterAbortRejected.cs` |
| `s3.multipart-list-uploads-then-abort-nonexistent` | `4df2823` | ported | `S3/MultipartListUploadsThenAbortNonexistent.cs` |
| `s3.multipart-part-number-ordering` | `4df2823` | ported | `S3/MultipartPartNumberOrdering.cs` |
| `s3.multipart-upload-and-presigned-get-round-trip` | `4df2823` | ported | `S3/MultipartUploadAndPresignedGetRoundTrip.cs` |
| `s3.multipart-upload-complete` | `4df2823` | ported | `S3/MultipartUploadComplete.cs` |
| `s3.multipart-upload-complete-wrong-part-order-rejected` | `4df2823` | ported | `S3/MultipartUploadCompleteWrongPartOrderRejected.cs` |
| `s3.multipart-upload-concurrent-part-overwrite` | `4df2823` | ported | `S3/MultipartUploadConcurrentPartOverwrite.cs` |
| `s3.multipart-upload-part-invalid-number-rejected` | `4df2823` | ported | `S3/MultipartUploadPartInvalidNumberRejected.cs` |
| `s3.multipart-upload-visible-in-list-until-complete` | `4df2823` | ported | `S3/MultipartUploadVisibleInListUntilComplete.cs` |
| `s3.multipart-upload-with-sparse-part-numbers-uploaded-in-reverse-round-trips` | `4df2823` | ported | `S3/MultipartUploadWithSparsePartNumbersUploadedInReverseRoundTrips.cs` |
| `s3.notification-fifo-destination-rejected` | `4df2823` | ported | `S3/NotificationFifoDestinationRejected.cs` |
| `s3.notification-filter-non-matching-key-not-delivered` | `4df2823` | ported | `S3/NotificationFilterNonMatchingKeyNotDelivered.cs` |
| `s3.notification-invalid-destination-rejected` | `4df2823` | ported | `S3/NotificationInvalidDestinationRejected.cs` |
| `s3.notification-sqs-test-event-format` | `4df2823` | ported | `S3/NotificationSqsTestEventFormat.cs` |
| `s3.notification-to-sqs` | `4df2823` | ported | `S3/NotificationToSqs.cs` |
| `s3.object-acl-canned-roundtrip` | `4df2823` | ported | `S3/ObjectAclCannedRoundtrip.cs` |
| `s3.object-acl-lifecycle-nonexistent-key` | `4df2823` | ported | `S3/ObjectAclLifecycleNonexistentKey.cs` |
| `s3.object-acl-lifecycle-nosuchkey` | `4df2823` | ported | `S3/ObjectAclLifecycleNosuchkey.cs` |
| `s3.object-annotation-crud-lifecycle` | `4df2823` | ported | `S3/ObjectAnnotationCrudLifecycle.cs` |
| `s3.object-annotation-full-crud-lifecycle` | `4df2823` | ported | `S3/ObjectAnnotationFullCrudLifecycle.cs` |
| `s3.object-annotation-name-too-long-rejected` | `4df2823` | ported | `S3/ObjectAnnotationNameTooLongRejected.cs` |
| `s3.object-annotation-not-found-errors` | `4df2823` | ported | `S3/ObjectAnnotationNotFoundErrors.cs` |
| `s3.object-annotation-reserved-prefix-rejected` | `4df2823` | ported | `S3/ObjectAnnotationReservedPrefixRejected.cs` |
| `s3.object-annotation-roundtrip` | `4df2823` | ported | `S3/ObjectAnnotationRoundtrip.cs` |
| `s3.object-annotation-validation-errors-rejected` | `4df2823` | ported | `S3/ObjectAnnotationValidationErrorsRejected.cs` |
| `s3.object-attributes-reflects-state` | `4df2823` | ported | `S3/ObjectAttributesReflectsState.cs` |
| `s3.object-attributes-returns-metadata` | `4df2823` | ported | `S3/ObjectAttributesReturnsMetadata.cs` |
| `s3.object-key-1024-bytes-boundary-rejected` | `4df2823` | ported | `S3/ObjectKey1024BytesBoundaryRejected.cs` |
| `s3.object-legal-hold-blocks-delete` | `4df2823` | ported | `S3/ObjectLegalHoldBlocksDelete.cs` |
| `s3.object-legal-hold-lifecycle` | `4df2823` | ported | `S3/ObjectLegalHoldLifecycle.cs` |
| `s3.object-legal-hold-requires-object-lock-rejected` | `4df2823` | ported | `S3/ObjectLegalHoldRequiresObjectLockRejected.cs` |
| `s3.object-legal-hold-roundtrip` | `4df2823` | ported | `S3/ObjectLegalHoldRoundtrip.cs` |
| `s3.object-lock-configuration-roundtrip` | `4df2823` | ported | `S3/ObjectLockConfigurationRoundtrip.cs` |
| `s3.object-lock-enable-on-versioned-bucket` | `4df2823` | ported | `S3/ObjectLockEnableOnVersionedBucket.cs` |
| `s3.object-lock-event-hold-requires-mode-duration-and-floor-date-rejected` | `4df2823` | ported | `S3/ObjectLockEventHoldRequiresModeDurationAndFloorDateRejected.cs` |
| `s3.object-lock-ops-require-lock-enabled-bucket` | `4df2823` | ported | `S3/ObjectLockOpsRequireLockEnabledBucket.cs` |
| `s3.object-lock-requires-versioning-rejected` | `4df2823` | ported | `S3/ObjectLockRequiresVersioningRejected.cs` |
| `s3.object-lock-subresource-puts-leave-the-object-body-and-versions-intact` | `4df2823` | ported | `S3/ObjectLockSubresourcePutsLeaveTheObjectBodyAndVersionsIntact.cs` |
| `s3.object-metadata-unicode-value-round-trip` | `4df2823` | ported | `S3/ObjectMetadataUnicodeValueRoundTrip.cs` |
| `s3.object-ownership-enforced-blocks-object-acl` | `4df2823` | ported | `S3/ObjectOwnershipEnforcedBlocksObjectAcl.cs` |
| `s3.object-put-get-delete` | `4df2823` | ported | `S3/ObjectPutGetDelete.cs` |
| `s3.object-retention-compliance-cannot-shorten` | `4df2823` | ported | `S3/ObjectRetentionComplianceCannotShorten.cs` |
| `s3.object-retention-governance-blocks-delete` | `4df2823` | ported | `S3/ObjectRetentionGovernanceBlocksDelete.cs` |
| `s3.object-retention-governance-bypass-delete` | `4df2823` | ported | `S3/ObjectRetentionGovernanceBypassDelete.cs` |
| `s3.object-retention-lifecycle` | `4df2823` | ported | `S3/ObjectRetentionLifecycle.cs` |
| `s3.object-retention-roundtrip` | `4df2823` | ported | `S3/ObjectRetentionRoundtrip.cs` |
| `s3.object-retention-shorten-governance-without-bypass-rejected` | `4df2823` | ported | `S3/ObjectRetentionShortenGovernanceWithoutBypassRejected.cs` |
| `s3.object-retention-until-date-enforces-window` | `4df2823` | ported | `S3/ObjectRetentionUntilDateEnforcesWindow.cs` |
| `s3.object-storage-class-reflected` | `4df2823` | ported | `S3/ObjectStorageClassReflected.cs` |
| `s3.object-system-metadata-roundtrip` | `4df2823` | ported | `S3/ObjectSystemMetadataRoundtrip.cs` |
| `s3.object-tagging-delete` | `4df2823` | ported | `S3/ObjectTaggingDelete.cs` |
| `s3.object-tagging-exceeds-limit-rejected` | `4df2823` | ported | `S3/ObjectTaggingExceedsLimitRejected.cs` |
| `s3.object-tagging-roundtrip` | `4df2823` | ported | `S3/ObjectTaggingRoundtrip.cs` |
| `s3.object-torrent-retrieval` | `4df2823` | ported | `S3/ObjectTorrentRetrieval.cs` |
| `s3.object-user-metadata-keys-lowercased` | `4df2823` | ported | `S3/ObjectUserMetadataKeysLowercased.cs` |
| `s3.objects-totalling-over-128-mib-across-three-buckets-all-read-back` | `4df2823` | ported | `S3/ObjectsTotallingOver128MibAcrossThreeBucketsAllReadBack.cs` |
| `s3.op-contract-delete-bucket` | `4df2823` | ported | `S3/OpContractDeleteBucket.cs` |
| `s3.op-contract-delete-bucket-cors` | `4df2823` | ported | `S3/OpContractDeleteBucketCors.cs` |
| `s3.op-contract-delete-bucket-lifecycle` | `4df2823` | ported | `S3/OpContractDeleteBucketLifecycle.cs` |
| `s3.op-contract-delete-bucket-metrics-configuration` | `4df2823` | ported | `S3/OpContractDeleteBucketMetricsConfiguration.cs` |
| `s3.op-contract-delete-bucket-policy` | `4df2823` | ported | `S3/OpContractDeleteBucketPolicy.cs` |
| `s3.op-contract-delete-bucket-tagging` | `4df2823` | ported | `S3/OpContractDeleteBucketTagging.cs` |
| `s3.op-contract-delete-object-tagging` | `4df2823` | ported | `S3/OpContractDeleteObjectTagging.cs` |
| `s3.op-contract-delete-objects` | `4df2823` | ported | `S3/OpContractDeleteObjects.cs` |
| `s3.op-contract-delete-public-access-block` | `4df2823` | ported | `S3/OpContractDeletePublicAccessBlock.cs` |
| `s3.op-contract-get-bucket-cors` | `4df2823` | ported | `S3/OpContractGetBucketCors.cs` |
| `s3.op-contract-get-bucket-lifecycle-configuration` | `4df2823` | ported | `S3/OpContractGetBucketLifecycleConfiguration.cs` |
| `s3.op-contract-get-bucket-location` | `4df2823` | ported | `S3/OpContractGetBucketLocation.cs` |
| `s3.op-contract-get-bucket-metrics-configuration` | `4df2823` | ported | `S3/OpContractGetBucketMetricsConfiguration.cs` |
| `s3.op-contract-get-bucket-notification-configuration` | `4df2823` | ported | `S3/OpContractGetBucketNotificationConfiguration.cs` |
| `s3.op-contract-get-bucket-policy` | `4df2823` | ported | `S3/OpContractGetBucketPolicy.cs` |
| `s3.op-contract-getBucketTagging` | `4df2823` | ported | `S3/OpContractGetBucketTagging.cs` |
| `s3.op-contract-getBucketVersioning` | `4df2823` | ported | `S3/OpContractGetBucketVersioning.cs` |
| `s3.op-contract-getObjectTagging` | `4df2823` | ported | `S3/OpContractGetObjectTagging.cs` |
| `s3.op-contract-getPublicAccessBlock` | `4df2823` | ported | `S3/OpContractGetPublicAccessBlock.cs` |
| `s3.op-contract-headBucket` | `4df2823` | ported | `S3/OpContractHeadBucket.cs` |
| `s3.op-contract-listBucketMetricsConfigurations` | `4df2823` | ported | `S3/OpContractListBucketMetricsConfigurations.cs` |
| `s3.op-contract-listObjectVersions` | `4df2823` | ported | `S3/OpContractListObjectVersions.cs` |
| `s3.op-contract-listObjectsV2` | `4df2823` | ported | `S3/OpContractListObjectsV2.cs` |
| `s3.op-contract-put-bucket-tagging-invalid-tag-rejected` | `4df2823` | ported | `S3/OpContractPutBucketTaggingInvalidTagRejected.cs` |
| `s3.op-contract-put-object-tagging-invalid-tag-rejected` | `4df2823` | ported | `S3/OpContractPutObjectTaggingInvalidTagRejected.cs` |
| `s3.op-contract-putBucketCors` | `4df2823` | ported | `S3/OpContractPutBucketCors.cs` |
| `s3.op-contract-putBucketLifecycleConfiguration` | `4df2823` | ported | `S3/OpContractPutBucketLifecycleConfiguration.cs` |
| `s3.op-contract-putBucketMetricsConfiguration` | `4df2823` | ported | `S3/OpContractPutBucketMetricsConfiguration.cs` |
| `s3.op-contract-putBucketNotificationConfiguration` | `4df2823` | not ported | Lambda functions, with IAM roles |
| `s3.op-contract-putBucketVersioning` | `4df2823` | ported | `S3/OpContractPutBucketVersioning.cs` |
| `s3.op-contract-uploadPartCopy` | `4df2823` | ported | `S3/OpContractUploadPartCopy.cs` |
| `s3.post-object-form-upload-enforces-its-policy-conditions` | `4df2823` | ported | `S3/PostObjectFormUploadEnforcesItsPolicyConditions.cs` |
| `s3.presigned-get` | `4df2823` | ported | `S3/PresignedGet.cs` |
| `s3.presigned-post-expired-policy-rejected-and-writes-nothing` | `4df2823` | ported | `S3/PresignedPostExpiredPolicyRejectedAndWritesNothing.cs` |
| `s3.presigned-post-form-upload-substitutes-filename-and-matches-content-type` | `4df2823` | ported | `S3/PresignedPostFormUploadSubstitutesFilenameAndMatchesContentType.cs` |
| `s3.presigned-put` | `4df2823` | ported | `S3/PresignedPut.cs` |
| `s3.presigned-put-requires-every-signed-header-and-refuses-an-over-week-expiry` | `4df2823` | ported | `S3/PresignedPutRequiresEverySignedHeaderAndRefusesAnOverWeekExpiry.cs` |
| `s3.presigned-upload-part-urls-assemble-a-multipart-object-read-by-presigned-ranged-get` | `4df2823` | ported | `S3/PresignedUploadPartUrlsAssembleAMultipartObjectReadByPresignedRangedGet.cs` |
| `s3.public-access-block` | `4df2823` | ported | `S3/PublicAccessBlock.cs` |
| `s3.public-access-block-blocks-public-acl` | `4df2823` | ported | `S3/PublicAccessBlockBlocksPublicAcl.cs` |
| `s3.public-access-block-rejects-public-policy` | `4df2823` | ported | `S3/PublicAccessBlockRejectsPublicPolicy.cs` |
| `s3.public-access-block-state-machine` | `4df2823` | ported | `S3/PublicAccessBlockStateMachine.cs` |
| `s3.put-bucket-metrics-configuration-invalid-id-rejected` | `4df2823` | ported | `S3/PutBucketMetricsConfigurationInvalidIdRejected.cs` |
| `s3.put-get-roundtrip-binary` | `4df2823` | ported | `S3/PutGetRoundtripBinary.cs` |
| `s3.put-object-acl-no-such-key` | `4df2823` | ported | `S3/PutObjectAclNoSuchKey.cs` |
| `s3.put-object-annotation-limits` | `4df2823` | ported | `S3/PutObjectAnnotationLimits.cs` |
| `s3.put-object-append-conditional-offset-contention` | `4df2823` | ported | `S3/PutObjectAppendConditionalOffsetContention.cs` |
| `s3.put-object-append-write-offset-rejected` | `4df2823` | ported | `S3/PutObjectAppendWriteOffsetRejected.cs` |
| `s3.put-object-bad-content-md5-rejected` | `4df2823` | ported | `S3/PutObjectBadContentMd5Rejected.cs` |
| `s3.put-object-conditional-write-optimistic-lock` | `4df2823` | ported | `S3/PutObjectConditionalWriteOptimisticLock.cs` |
| `s3.put-object-encryption-type-mismatch` | `4df2823` | ported | `S3/PutObjectEncryptionTypeMismatch.cs` |
| `s3.put-object-encryption-type-mismatch-rejected` | `4df2823` | ported | `S3/PutObjectEncryptionTypeMismatchRejected.cs` |
| `s3.put-object-if-match-conditional` | `4df2823` | ported | `S3/PutObjectIfMatchConditional.cs` |
| `s3.put-object-if-match-matches-an-etag-restored-by-rewriting-identical-bytes` | `4df2823` | ported | `S3/PutObjectIfMatchMatchesAnEtagRestoredByRewritingIdenticalBytes.cs` |
| `s3.put-object-if-match-refuses-a-composite-etag-replaced-by-another-multipart-upload` | `4df2823` | ported | `S3/PutObjectIfMatchRefusesACompositeEtagReplacedByAnotherMultipartUpload.cs` |
| `s3.put-object-if-match-takes-a-multipart-etag-and-refuses-it-once-stale` | `4df2823` | ported | `S3/PutObjectIfMatchTakesAMultipartEtagAndRefusesItOnceStale.cs` |
| `s3.put-object-if-none-match-precondition` | `4df2823` | ported | `S3/PutObjectIfNoneMatchPrecondition.cs` |
| `s3.put-object-invalid-request-rejected` | `4df2823` | ported | `S3/PutObjectInvalidRequestRejected.cs` |
| `s3.put-object-invalid-write-offset` | `4df2823` | ported | `S3/PutObjectInvalidWriteOffset.cs` |
| `s3.put-object-invalid-write-offset-rejected` | `4df2823` | ported | `S3/PutObjectInvalidWriteOffsetRejected.cs` |
| `s3.put-object-policy-requires-if-none-match-header` | `4df2823` | ported | `S3/PutObjectPolicyRequiresIfNoneMatchHeader.cs` |
| `s3.put-object-sha1-checksum-round-trip-and-bad-digest-rejected` | `4df2823` | ported | `S3/PutObjectSha1ChecksumRoundTripAndBadDigestRejected.cs` |
| `s3.put-object-sse-kms-encryption-context-round-trips-then-malformed-rejected` | `4df2823` | not ported | KMS keys |
| `s3.range-read-byte-range` | `4df2823` | ported | `S3/RangeReadByteRange.cs` |
| `s3.range-read-middle` | `4df2823` | ported | `S3/RangeReadMiddle.cs` |
| `s3.range-read-open-end` | `4df2823` | ported | `S3/RangeReadOpenEnd.cs` |
| `s3.range-read-suffix` | `4df2823` | ported | `S3/RangeReadSuffix.cs` |
| `s3.range-read-unsatisfiable-rejected` | `4df2823` | ported | `S3/RangeReadUnsatisfiableRejected.cs` |
| `s3.rename-object-concurrent-idempotency` | `4df2823` | ported | `S3/RenameObjectConcurrentIdempotency.cs` |
| `s3.rename-object-destination-exists-rejected` | `4df2823` | ported | `S3/RenameObjectDestinationExistsRejected.cs` |
| `s3.rename-object-idempotency-mismatch` | `4df2823` | ported | `S3/RenameObjectIdempotencyMismatch.cs` |
| `s3.rename-object-idempotency-token` | `4df2823` | ported | `S3/RenameObjectIdempotencyToken.cs` |
| `s3.rename-object-idempotency-token-mismatch` | `4df2823` | ported | `S3/RenameObjectIdempotencyTokenMismatch.cs` |
| `s3.rename-object-lifecycle` | `4df2823` | ported | `S3/RenameObjectLifecycle.cs` |
| `s3.rename-object-source-if-none-match-precondition-rejected` | `4df2823` | ported | `S3/RenameObjectSourceIfNoneMatchPreconditionRejected.cs` |
| `s3.rename-object-state-transition` | `4df2823` | ported | `S3/RenameObjectStateTransition.cs` |
| `s3.replication-covers-the-eligible-prefix-only-and-withholds-the-delete-marker` | `4df2823` | ported | `S3/ReplicationCoversTheEligiblePrefixOnlyAndWithholdsTheDeleteMarker.cs` |
| `s3.replication-server-side-execution` | `4df2823` | ported | `S3/ReplicationServerSideExecution.cs` |
| `s3.requester-pays-enforcement` | `4df2823` | ported | `S3/RequesterPaysEnforcement.cs` |
| `s3.restore-archived-object-active-tier-error` | `4df2823` | ported | `S3/RestoreArchivedObjectActiveTierError.cs` |
| `s3.restore-object-duplicate-in-progress-rejected` | `4df2823` | ported | `S3/RestoreObjectDuplicateInProgressRejected.cs` |
| `s3.restore-object-glacier-transition` | `4df2823` | ported | `S3/RestoreObjectGlacierTransition.cs` |
| `s3.restore-object-on-standard-rejected` | `4df2823` | ported | `S3/RestoreObjectOnStandardRejected.cs` |
| `s3.restore-object-server-side-tier-transition` | `4df2823` | ported | `S3/RestoreObjectServerSideTierTransition.cs` |
| `s3.restore-object-temporary-availability-window` | `4df2823` | ported | `S3/RestoreObjectTemporaryAvailabilityWindow.cs` |
| `s3.restore-object-ttl-expiry-reverts` | `4df2823` | ported | `S3/RestoreObjectTtlExpiryReverts.cs` |
| `s3.s3-chunked-upload-missing-decoded-content-length-rejected-object-absent` | `4df2823` | ported | `S3/S3ChunkedUploadMissingDecodedContentLengthRejectedObjectAbsent.cs` |
| `s3.s3-chunked-upload-trailer-checksum-mismatch-rejected-object-absent` | `4df2823` | ported | `S3/S3ChunkedUploadTrailerChecksumMismatchRejectedObjectAbsent.cs` |
| `s3.s3-chunked-upload-trailer-checksum-stores-object-and-checksum` | `4df2823` | ported | `S3/S3ChunkedUploadTrailerChecksumStoresObjectAndChecksum.cs` |
| `s3.s3-chunked-upload-trailer-name-mismatch-rejected-object-absent` | `4df2823` | ported | `S3/S3ChunkedUploadTrailerNameMismatchRejectedObjectAbsent.cs` |
| `s3.s3-chunked-upload-wrong-chunk-signature-rejected-object-absent` | `4df2823` | ported | `S3/S3ChunkedUploadWrongChunkSignatureRejectedObjectAbsent.cs` |
| `s3.s3-complete-multipart-with-a-wrong-full-object-checksum-is-rejected-and-no-object-exists` | `4df2823` | ported | `S3/S3CompleteMultipartWithAWrongFullObjectChecksumIsRejectedAndNoObjectExists.cs` |
| `s3.s3-concurrent-copy-while-the-source-is-being-deleted-never-yields-a-partial-object` | `4df2823` | ported | `S3/S3ConcurrentCopyWhileTheSourceIsBeingDeletedNeverYieldsAPartialObject.cs` |
| `s3.s3-conditional-precedence-and-lifecycle-headers-copy` | `4df2823` | ported | `S3/S3ConditionalPrecedenceAndLifecycleHeadersCopy.cs` |
| `s3.s3-conditional-precedence-and-lifecycle-headers-get` | `4df2823` | ported | `S3/S3ConditionalPrecedenceAndLifecycleHeadersGet.cs` |
| `s3.s3-conditional-precedence-and-lifecycle-headers-lifecycle-mpu` | `4df2823` | ported | `S3/S3ConditionalPrecedenceAndLifecycleHeadersLifecycleMpu.cs` |
| `s3.s3-conditional-precedence-and-lifecycle-headers-mpu-parts` | `4df2823` | ported | `S3/S3ConditionalPrecedenceAndLifecycleHeadersMpuParts.cs` |
| `s3.s3-delete-marker-hides-the-object-while-the-version-still-returns-its-bytes` | `4df2823` | ported | `S3/S3DeleteMarkerHidesTheObjectWhileTheVersionStillReturnsItsBytes.cs` |
| `s3.s3-delete-object-tagging-clears-only-the-addressed-version-tag-set` | `4df2823` | ported | `S3/S3DeleteObjectTaggingClearsOnlyTheAddressedVersionTagSet.cs` |
| `s3.s3-delete-object-with-a-stale-if-match-etag-is-refused-and-the-object-survives` | `4df2823` | ported | `S3/S3DeleteObjectWithAStaleIfMatchEtagIsRefusedAndTheObjectSurvives.cs` |
| `s3.s3-delete-objects-batch` | `4df2823` | ported | `S3/S3DeleteObjectsBatch.cs` |
| `s3.s3-delete-objects-reports-every-requested-key-version-pair-exactly-once` | `4df2823` | ported | `S3/S3DeleteObjectsReportsEveryRequestedKeyVersionPairExactlyOnce.cs` |
| `s3.s3-express-append-produces-the-exact-concatenation-of-three-offset-writes` | `4df2823` | ported | `S3/S3ExpressAppendProducesTheExactConcatenationOfThreeOffsetWrites.cs` |
| `s3.s3-full-object-crc64nvme-over-multipart-equals-the-whole-object-checksum` | `4df2823` | ported | `S3/S3FullObjectCrc64nvmeOverMultipartEqualsTheWholeObjectChecksum.cs` |
| `s3.s3-if-none-match-star-put-is-atomic-across-a-delete-marker` | `4df2823` | ported | `S3/S3IfNoneMatchStarPutIsAtomicAcrossADeleteMarker.cs` |
| `s3.s3-lcp-rejects-inverted-or-equal-object-size-range` | `4df2823` | ported | `S3/S3LcpRejectsInvertedOrEqualObjectSizeRange.cs` |
| `s3.s3-object-lock-compliance-cannot-be-bypassed-while-governance-can` | `4df2823` | ported | `S3/S3ObjectLockComplianceCannotBeBypassedWhileGovernanceCan.cs` |
| `s3.s3-put-object-with-a-deliberately-wrong-crc32-is-rejected-and-the-key-is-absent` | `4df2823` | ported | `S3/S3PutObjectWithADeliberatelyWrongCrc32IsRejectedAndTheKeyIsAbsent.cs` |
| `s3.s3-refused-conditional-gets-create-no-version-and-leave-the-object-intact` | `4df2823` | ported | `S3/S3RefusedConditionalGetsCreateNoVersionAndLeaveTheObjectIntact.cs` |
| `s3.s3-sse-kms-object-is-unreadable-while-its-cmk-is-disabled-and-readable-again-after` | `4df2823` | not ported | KMS keys |
| `s3.s3-ssec-and-endpoint-enforcement-secure-transport-deny-policy-refuses-plaintext-http-and-allows-https` | `4df2823` | known gap: SecureTransport | `S3/S3SsecAndEndpointEnforcementSecureTransportDenyPolicyRefusesPlaintextHttpAndAllowsHttps.cs` |
| `s3.s3-ssec-and-endpoint-enforcement-ssec-copy-to-sse-s3-without-the-copy-source-key-rejected` | `4df2823` | ported | `S3/S3SsecAndEndpointEnforcementSsecCopyToSseS3WithoutTheCopySourceKeyRejected.cs` |
| `s3.s3-ssec-and-endpoint-enforcement-ssec-multipart-upload-round-trips-only-with-the-customer-key` | `4df2823` | ported | `S3/S3SsecAndEndpointEnforcementSsecMultipartUploadRoundTripsOnlyWithTheCustomerKey.cs` |
| `s3.s3-ssec-and-endpoint-enforcement-ssec-request-over-plain-http-rejected` | `4df2823` | ported | `S3/S3SsecAndEndpointEnforcementSsecRequestOverPlainHttpRejected.cs` |
| `s3.s3-ssec-and-endpoint-enforcement-ssec-upload-part-copy-rekeys-an-ssec-source-into-a-multipart-object` | `4df2823` | ported | `S3/S3SsecAndEndpointEnforcementSsecUploadPartCopyRekeysAnSsecSourceIntoAMultipartObject.cs` |
| `s3.s3-ssec-and-endpoint-enforcement-website-endpoint-serving-an-ssec-object-rejected` | `4df2823` | ported | `S3/S3SsecAndEndpointEnforcementWebsiteEndpointServingAnSsecObjectRejected.cs` |
| `s3.s3-ssec-and-endpoint-enforcement-website-redirect-location-answers-301-while-rest-serves-the-body` | `4df2823` | ported | `S3/S3SsecAndEndpointEnforcementWebsiteRedirectLocationAnswers301WhileRestServesTheBody.cs` |
| `s3.s3-ssec-and-endpoint-enforcement-website-routing-rule-on-404-redirects-missing-keys` | `4df2823` | ported | `S3/S3SsecAndEndpointEnforcementWebsiteRoutingRuleOn404RedirectsMissingKeys.cs` |
| `s3.s3-strong-read-after-write` | `4df2823` | ported | `S3/S3StrongReadAfterWrite.cs` |
| `s3.s3-website-hosting-bare-folder-path-redirects-to-trailing-slash` | `4df2823` | ported | `S3/S3WebsiteHostingBareFolderPathRedirectsToTrailingSlash.cs` |
| `s3.s3-website-hosting-missing-key-with-no-error-document-is-rejected-with-the-default-error-page` | `4df2823` | ported | `S3/S3WebsiteHostingMissingKeyWithNoErrorDocumentIsRejectedWithTheDefaultErrorPage.cs` |
| `s3.s3-website-hosting-redirect-all-requests-to-answers-301-to-the-target-host` | `4df2823` | ported | `S3/S3WebsiteHostingRedirectAllRequestsToAnswers301ToTheTargetHost.cs` |
| `s3.s3checksum-copy-object-computes-the-requested-algorithm-from-the-bytes` | `4df2823` | ported | `S3/S3checksumCopyObjectComputesTheRequestedAlgorithmFromTheBytes.cs` |
| `s3.s3checksum-crc32c-mismatch-rejected-and-no-object-lands` | `4df2823` | ported | `S3/S3checksumCrc32cMismatchRejectedAndNoObjectLands.cs` |
| `s3.s3checksum-digest-is-a-property-of-the-bytes-in-a-derived-second-region` | `4df2823` | ported | `S3/S3checksumDigestIsAPropertyOfTheBytesInADerivedSecondRegion.cs` |
| `s3.s3checksum-incompatible-algorithm-and-type-pairs-rejected` | `4df2823` | ported | `S3/S3checksumIncompatibleAlgorithmAndTypePairsRejected.cs` |
| `s3.s3checksum-malformed-header-rejected-as-invalid-request-not-baddigest` | `4df2823` | ported | `S3/S3checksumMalformedHeaderRejectedAsInvalidRequestNotBaddigest.cs` |
| `s3.s3checksum-part-scoped-read-returns-the-part-digest-not-the-composite` | `4df2823` | ported | `S3/S3checksumPartScopedReadReturnsThePartDigestNotTheComposite.cs` |
| `s3.s3checksum-presigned-put-query-crc32-mismatch-then-match` | `4df2823` | ported | `S3/S3checksumPresignedPutQueryCrc32MismatchThenMatch.cs` |
| `s3.s3checksum-presigned-put-query-malformed-base64-rejected` | `4df2823` | ported | `S3/S3checksumPresignedPutQueryMalformedBase64Rejected.cs` |
| `s3.s3checksum-presigned-put-query-sha256-mismatch-then-match` | `4df2823` | ported | `S3/S3checksumPresignedPutQuerySha256MismatchThenMatch.cs` |
| `s3.s3checksum-sha256-mismatch-refuses-the-overwrite-and-the-stored-bytes-survive` | `4df2823` | ported | `S3/S3checksumSha256MismatchRefusesTheOverwriteAndTheStoredBytesSurvive.cs` |
| `s3.s3checksum-upload-part-mismatch-rejected-and-the-part-is-absent` | `4df2823` | ported | `S3/S3checksumUploadPartMismatchRejectedAndThePartIsAbsent.cs` |
| `s3.s3crr-a-version-delete-is-never-replicated-even-with-marker-replication-enabled` | `4df2823` | ported | `S3/S3crrAVersionDeleteIsNeverReplicatedEvenWithMarkerReplicationEnabled.cs` |
| `s3.s3crr-a-write-to-the-replica-never-propagates-back-to-the-source` | `4df2823` | ported | `S3/S3crrAWriteToTheReplicaNeverPropagatesBackToTheSource.cs` |
| `s3.s3crr-delete-marker-replicates-only-where-marker-replication-is-enabled` | `4df2823` | ported | `S3/S3crrDeleteMarkerReplicatesOnlyWhereMarkerReplicationIsEnabled.cs` |
| `s3.s3crr-new-object-replicates-byte-identical-to-a-derived-peer-region` | `4df2823` | ported | `S3/S3crrNewObjectReplicatesByteIdenticalToADerivedPeerRegion.cs` |
| `s3.s3crr-object-written-before-the-rule-is-never-backfilled` | `4df2823` | ported | `S3/S3crrObjectWrittenBeforeTheRuleIsNeverBackfilled.cs` |
| `s3.s3crr-replication-fails-visibly-when-the-role-cannot-write-the-destination` | `4df2823` | not ported | IAM role and identity-policy enforcement |
| `s3.s3unicode-a-nul-byte-in-an-object-key-is-rejected` | `4df2823` | ported | `S3/S3unicodeANulByteInAnObjectKeyIsRejected.cs` |
| `s3.s3unicode-astral-emoji-key-and-opaque-body-are-byte-exact` | `4df2823` | ported | `S3/S3unicodeAstralEmojiKeyAndOpaqueBodyAreByteExact.cs` |
| `s3.s3unicode-carriage-return-in-a-key-survives-the-list-xml-byte-exact` | `4df2823` | ported | `S3/S3unicodeCarriageReturnInAKeySurvivesTheListXmlByteExact.cs` |
| `s3.s3unicode-combining-marks-in-a-key-are-not-normalised` | `4df2823` | ported | `S3/S3unicodeCombiningMarksInAKeyAreNotNormalised.cs` |
| `s3.s3unicode-copy-source-with-plus-and-percent-resolves-literally` | `4df2823` | ported | `S3/S3unicodeCopySourceWithPlusAndPercentResolvesLiterally.cs` |
| `s3.s3unicode-delete-objects-xml-rejects-a-control-char-a-put-key-accepts` | `4df2823` | ported | `S3/S3unicodeDeleteObjectsXmlRejectsAControlCharAPutKeyAccepts.cs` |
| `s3.s3unicode-leading-and-trailing-spaces-in-a-key-are-preserved` | `4df2823` | ported | `S3/S3unicodeLeadingAndTrailingSpacesInAKeyArePreserved.cs` |
| `s3.s3unicode-list-objects-url-encoding-is-form-encoded-not-rfc3986` | `4df2823` | ported | `S3/S3unicodeListObjectsUrlEncodingIsFormEncodedNotRfc3986.cs` |
| `s3.s3unicode-list-objects-with-an-unsupported-encoding-type-is-rejected` | `4df2823` | ported | `S3/S3unicodeListObjectsWithAnUnsupportedEncodingTypeIsRejected.cs` |
| `s3.s3unicode-object-tag-value-with-an-astral-emoji-rejected` | `4df2823` | ported | `S3/S3unicodeObjectTagValueWithAnAstralEmojiRejected.cs` |
| `s3.s3unicode-rfc3986-reserved-chars-in-a-key-are-literal` | `4df2823` | ported | `S3/S3unicodeRfc3986ReservedCharsInAKeyAreLiteral.cs` |
| `s3.s3x-s3express-session-and-listing-cross-bucket-token-denied` | `4df2823` | ported | `S3/S3xS3expressSessionAndListingCrossBucketTokenDenied.cs` |
| `s3.s3x-s3express-session-and-listing-expired-token-denied` | `4df2823` | ported | `S3/S3xS3expressSessionAndListingExpiredTokenDenied.cs` |
| `s3.s3x-s3express-session-and-listing-inflight-mpu-prefix-listed` | `4df2823` | ported | `S3/S3xS3expressSessionAndListingInflightMpuPrefixListed.cs` |
| `s3.s3x-s3express-session-and-listing-non-slash-delimiter-rejected` | `4df2823` | ported | `S3/S3xS3expressSessionAndListingNonSlashDelimiterRejected.cs` |
| `s3.s3x-s3express-session-and-listing-readonly-session-put-denied` | `4df2823` | ported | `S3/S3xS3expressSessionAndListingReadonlySessionPutDenied.cs` |
| `s3.s3x-s3express-session-and-listing-readwrite-session-round-trip` | `4df2823` | ported | `S3/S3xS3expressSessionAndListingReadwriteSessionRoundTrip.cs` |
| `s3.s3x-s3express-session-and-listing-start-after-rejected` | `4df2823` | ported | `S3/S3xS3expressSessionAndListingStartAfterRejected.cs` |
| `s3.s3x-s3express-session-and-listing-unterminated-prefix-rejected` | `4df2823` | ported | `S3/S3xS3expressSessionAndListingUnterminatedPrefixRejected.cs` |
| `s3.secure-transport-policy-plain-http-denied` | `4df2823` | known gap: SecureTransport | `S3/SecureTransportPolicyPlainHttpDenied.cs` |
| `s3.select-object-content-permission-denied` | `4df2823` | not ported | IAM roles, users and identity policies |
| `s3.sigv4skew-cross-region-credential-scope-rejected` | `4df2823` | ported | `S3/Sigv4skewCrossRegionCredentialScopeRejected.cs` |
| `s3.sigv4skew-future-dated-put-outside-window-rejected` | `4df2823` | ported | `S3/Sigv4skewFutureDatedPutOutsideWindowRejected.cs` |
| `s3.sigv4skew-past-dated-put-outside-window-rejected` | `4df2823` | ported | `S3/Sigv4skewPastDatedPutOutsideWindowRejected.cs` |
| `s3.sigv4skew-presigned-get-altered-expires-param-rejected` | `4df2823` | ported | `S3/Sigv4skewPresignedGetAlteredExpiresParamRejected.cs` |
| `s3.sigv4skew-presigned-get-date-shifted-one-second-rejected` | `4df2823` | ported | `S3/Sigv4skewPresignedGetDateShiftedOneSecondRejected.cs` |
| `s3.sigv4skew-presigned-get-refusal-names-its-expiry-window` | `4df2823` | ported | `S3/Sigv4skewPresignedGetRefusalNamesItsExpiryWindow.cs` |
| `s3.sigv4skew-presigned-put-after-expiry-writes-no-object-rejected` | `4df2823` | ported | `S3/Sigv4skewPresignedPutAfterExpiryWritesNoObjectRejected.cs` |
| `s3.sigv4skew-window-enforced-in-derived-second-region-rejected` | `4df2823` | ported | `S3/Sigv4skewWindowEnforcedInDerivedSecondRegionRejected.cs` |
| `s3.sse-c-wrong-customer-key-rejected` | `4df2823` | ported | `S3/SseCWrongCustomerKeyRejected.cs` |
| `s3.sse-kms` | `4df2823` | not ported | KMS keys |
| `s3.sse-s3-default` | `4df2823` | ported | `S3/SseS3Default.cs` |
| `s3.sse-s3-explicit` | `4df2823` | ported | `S3/SseS3Explicit.cs` |
| `s3.ssec-head-object-requires-the-matching-customer-key` | `4df2823` | ported | `S3/SsecHeadObjectRequiresTheMatchingCustomerKey.cs` |
| `s3.storage-class-reduced-redundancy` | `4df2823` | ported | `S3/StorageClassReducedRedundancy.cs` |
| `s3.storage-class-standard` | `4df2823` | ported | `S3/StorageClassStandard.cs` |
| `s3.update-object-encryption-error-paths` | `4df2823` | not ported | KMS keys |
| `s3.update-object-encryption-error-preconditions` | `4df2823` | not ported | KMS keys |
| `s3.update-object-encryption-errors` | `4df2823` | not ported | KMS keys |
| `s3.upload-part-copy-source-if-match-takes-a-composite-etag-and-refuses-it-once-stale` | `4df2823` | ported | `S3/UploadPartCopySourceIfMatchTakesACompositeEtagAndRefusesItOnceStale.cs` |
| `s3.upload-part-of-twenty-mib-completes-and-reads-back-exactly` | `4df2823` | ported | `S3/UploadPartOfTwentyMibCompletesAndReadsBackExactly.cs` |
| `s3.versioning-delete-marker` | `4df2823` | ported | `S3/VersioningDeleteMarker.cs` |
| `s3.versioning-enable` | `4df2823` | ported | `S3/VersioningEnable.cs` |
| `s3.versioning-get-specific-version` | `4df2823` | ported | `S3/VersioningGetSpecificVersion.cs` |
| `s3.versioning-produces-version-ids` | `4df2823` | ported | `S3/VersioningProducesVersionIds.cs` |
| `s3.versioning-suspended-null-version` | `4df2823` | ported | `S3/VersioningSuspendedNullVersion.cs` |
| `s3.website-endpoint-serves-index-and-error-documents-and-refuses-writes` | `4df2823` | ported | `S3/WebsiteEndpointServesIndexAndErrorDocumentsAndRefusesWrites.cs` |

## behavioral/s3__sqs

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `s3__sqs.a-denied-put-creates-no-object-and-emits-no-notification` | `4df2823` | ported | `S3Sqs/ADeniedPutCreatesNoObjectAndEmitsNoNotification.cs` |
| `s3__sqs.an-aborted-multipart-upload-emits-no-object-created-notification` | `4df2823` | ported | `S3Sqs/AnAbortedMultipartUploadEmitsNoObjectCreatedNotification.cs` |
| `s3__sqs.deploy-s3-3-drift-versioning-suspended-out-of-band` | `4df2823` | not ported | CloudFormation stacks |
| `s3__sqs.deploy-s3-3-notification-nonexistent-queue-rollback-rejected` | `4df2823` | not ported | CloudFormation stacks |
| `s3__sqs.deploy-s3-3-notification-queue-without-policy-rollback-rejected` | `4df2823` | not ported | CloudFormation stacks |
| `s3__sqs.inline-tagging-and-canned-acl-on-write-withhold-tagging-and-acl-events` | `4df2823` | ported | `S3Sqs/InlineTaggingAndCannedAclOnWriteWithholdTaggingAndAclEvents.cs` |
| `s3__sqs.notification-destination-validation-rejects-unresolvable-queue` | `4df2823` | ported | `S3Sqs/NotificationDestinationValidationRejectsUnresolvableQueue.cs` |
| `s3__sqs.object-acl-put-event-reaches-only-its-queue` | `4df2823` | ported | `S3Sqs/ObjectAclPutEventReachesOnlyItsQueue.cs` |
| `s3__sqs.object-created-copy-event-reaches-only-its-queue` | `4df2823` | ported | `S3Sqs/ObjectCreatedCopyEventReachesOnlyItsQueue.cs` |
| `s3__sqs.object-tagging-delete-event-reaches-only-its-queue` | `4df2823` | ported | `S3Sqs/ObjectTaggingDeleteEventReachesOnlyItsQueue.cs` |
| `s3__sqs.object-tagging-put-event-reaches-only-its-queue` | `4df2823` | ported | `S3Sqs/ObjectTaggingPutEventReachesOnlyItsQueue.cs` |
| `s3__sqs.overlapping-object-tagging-wildcard-configuration-rejected` | `4df2823` | ported | `S3Sqs/OverlappingObjectTaggingWildcardConfigurationRejected.cs` |
| `s3__sqs.s3-notification-disjoint-prefix-routes-only-matching-queue` | `4df2823` | ported | `S3Sqs/S3NotificationDisjointPrefixRoutesOnlyMatchingQueue.cs` |
| `s3__sqs.s3-object-created-put-subscription-withholds-the-multipart-completion-event` | `4df2823` | ported | `S3Sqs/S3ObjectCreatedPutSubscriptionWithholdsTheMultipartCompletionEvent.cs` |
| `s3__sqs.s3-object-removed-delete-subscription-withholds-the-delete-marker-event` | `4df2823` | ported | `S3Sqs/S3ObjectRemovedDeleteSubscriptionWithholdsTheDeleteMarkerEvent.cs` |
| `s3__sqs.s3-sequencer-is-the-only-total-order-over-one-key` | `4df2823` | ported | `S3Sqs/S3SequencerIsTheOnlyTotalOrderOverOneKey.cs` |

## behavioral/s3__sns__sqs

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `s3__sns__sqs.s3-notification-without-topic-policy-rejected` | `4df2823` | ported | `S3SnsSqs/S3NotificationWithoutTopicPolicyRejected.cs` |
| `s3__sns__sqs.s3-object-created-notifies-sns` | `4df2823` | ported | `S3SnsSqs/S3ObjectCreatedNotifiesSns.cs` |

## behavioral/events__s3__sqs

| Check | Last changed | Status | Port or reason |
|---|---|---|---|
| `events__s3__sqs.a-post-object-upload-is-withheld-from-a-putobject-scoped-rule` | `4df2823` | ported | `EventsS3Sqs/APostObjectUploadIsWithheldFromAPutobjectScopedRule.cs` |
| `events__s3__sqs.cfn-s3-eventbridge-sqs-deploy` | `4df2823` | not ported | CloudFormation stacks |
| `events__s3__sqs.eventbridge-dlq-on-target-failure` | `4df2823` | known gap: EventBridgeUngrantedTargets | `EventsS3Sqs/EventbridgeDlqOnTargetFailure.cs` |
| `events__s3__sqs.eventbridge-fanout-multiple-sqs-targets` | `4df2823` | ported | `EventsS3Sqs/EventbridgeFanoutMultipleSqsTargets.cs` |
| `events__s3__sqs.eventbridge-fifo-queue-target-arn` | `4df2823` | ported | `EventsS3Sqs/EventbridgeFifoQueueTargetArn.cs` |
| `events__s3__sqs.eventbridge-full-envelope-fields-sqs` | `4df2823` | ported | `EventsS3Sqs/EventbridgeFullEnvelopeFieldsSqs.cs` |
| `events__s3__sqs.eventbridge-input-transformer-sqs` | `4df2823` | ported | `EventsS3Sqs/EventbridgeInputTransformerSqs.cs` |
| `events__s3__sqs.eventbridge-pattern-filtering-sqs` | `4df2823` | ported | `EventsS3Sqs/EventbridgePatternFilteringSqs.cs` |
| `events__s3__sqs.eventbridge-put-targets-invalid-queue-arn-rejected` | `4df2823` | ported | `EventsS3Sqs/EventbridgePutTargetsInvalidQueueArnRejected.cs` |
| `events__s3__sqs.eventbridge-rule-to-sqs-target` | `4df2823` | ported | `EventsS3Sqs/EventbridgeRuleToSqsTarget.cs` |
| `events__s3__sqs.eventbridge-target-without-queue-policy-silently-fails` | `4df2823` | known gap: EventBridgeUngrantedTargets | `EventsS3Sqs/EventbridgeTargetWithoutQueuePolicySilentlyFails.cs` |
| `events__s3__sqs.object-restore-initiated-and-tags-added-carry-their-own-detail-types` | `4df2823` | ported | `EventsS3Sqs/ObjectRestoreInitiatedAndTagsAddedCarryTheirOwnDetailTypes.cs` |
| `events__s3__sqs.obs-s3-3-acl-rejected-under-bucket-owner-enforced-emits-no-event` | `4df2823` | ported | `EventsS3Sqs/ObsS33AclRejectedUnderBucketOwnerEnforcedEmitsNoEvent.cs` |
| `events__s3__sqs.obs-s3-3-copy-object-reaches-both-paths-with-copy-reason` | `4df2823` | ported | `EventsS3Sqs/ObsS33CopyObjectReachesBothPathsWithCopyReason.cs` |
| `events__s3__sqs.obs-s3-3-copy-scoped-routes-withhold-a-plain-put` | `4df2823` | ported | `EventsS3Sqs/ObsS33CopyScopedRoutesWithholdAPlainPut.cs` |
| `events__s3__sqs.obs-s3-3-object-acl-put-reaches-both-paths` | `4df2823` | ported | `EventsS3Sqs/ObsS33ObjectAclPutReachesBothPaths.cs` |
| `events__s3__sqs.obs-s3-3-object-tagging-put-and-delete-reach-both-paths` | `4df2823` | ported | `EventsS3Sqs/ObsS33ObjectTaggingPutAndDeleteReachBothPaths.cs` |
| `events__s3__sqs.obs-s3-3-tags-deleted-withheld-from-tags-added-routes` | `4df2823` | ported | `EventsS3Sqs/ObsS33TagsDeletedWithheldFromTagsAddedRoutes.cs` |
| `events__s3__sqs.obs-s3-3-versioned-delete-marker-and-version-delete-reach-both-paths` | `4df2823` | ported | `EventsS3Sqs/ObsS33VersionedDeleteMarkerAndVersionDeleteReachBothPaths.cs` |
| `events__s3__sqs.removing-eventbridge-configuration-withholds-envelope-but-native-queue-still-delivers` | `4df2823` | ported | `EventsS3Sqs/RemovingEventbridgeConfigurationWithholdsEnvelopeButNativeQueueStillDelivers.cs` |
| `events__s3__sqs.s3-event-message-format-fields` | `4df2823` | ported | `EventsS3Sqs/S3EventMessageFormatFields.cs` |
| `events__s3__sqs.s3-event-wrong-type-not-delivered` | `4df2823` | ported | `EventsS3Sqs/S3EventWrongTypeNotDelivered.cs` |
| `events__s3__sqs.s3-eventbridge-not-enabled-no-delivery` | `4df2823` | ported | `EventsS3Sqs/S3EventbridgeNotEnabledNoDelivery.cs` |
| `events__s3__sqs.s3-eventbridge-to-sqs` | `4df2823` | ported | `EventsS3Sqs/S3EventbridgeToSqs.cs` |
| `events__s3__sqs.s3-multi-event-type-routing-sqs` | `4df2823` | ported | `EventsS3Sqs/S3MultiEventTypeRoutingSqs.cs` |
| `events__s3__sqs.s3-notification-to-sqs-direct` | `4df2823` | ported | `EventsS3Sqs/S3NotificationToSqsDirect.cs` |
| `events__s3__sqs.s3-notification-without-queue-policy-fails` | `4df2823` | ported | `EventsS3Sqs/S3NotificationWithoutQueuePolicyFails.cs` |
| `events__s3__sqs.s3-queue-notification-filter-delivers-only-matching-url-encoded-keys` | `4df2823` | ported | `EventsS3Sqs/S3QueueNotificationFilterDeliversOnlyMatchingUrlEncodedKeys.cs` |
| `events__s3__sqs.s3-queue-notification-names-copy-multipart-and-delete-events` | `4df2823` | ported | `EventsS3Sqs/S3QueueNotificationNamesCopyMultipartAndDeleteEvents.cs` |
| `events__s3__sqs.sqs-policy-wrong-source-arn-blocks-delivery` | `4df2823` | ported | `EventsS3Sqs/SqsPolicyWrongSourceArnBlocksDelivery.cs` |
| `events__s3__sqs.sqs-queue-policy-sourcearn-scoping` | `4df2823` | ported | `EventsS3Sqs/SqsQueuePolicySourcearnScoping.cs` |
| `events__s3__sqs.xsvc-s3-1-acl-update-and-tag-delete-route-to-their-own-rules` | `4df2823` | ported | `EventsS3Sqs/XsvcS31AclUpdateAndTagDeleteRouteToTheirOwnRules.cs` |
| `events__s3__sqs.xsvc-s3-1-input-transformer-renders-s3-uri-not-envelope` | `4df2823` | ported | `EventsS3Sqs/XsvcS31InputTransformerRendersS3UriNotEnvelope.cs` |
| `events__s3__sqs.xsvc-s3-1-key-anything-but-filter-withholds-excluded-keys` | `4df2823` | ported | `EventsS3Sqs/XsvcS31KeyAnythingButFilterWithholdsExcludedKeys.cs` |
| `events__s3__sqs.xsvc-s3-1-key-prefix-filter-withholds-near-miss-keys` | `4df2823` | ported | `EventsS3Sqs/XsvcS31KeyPrefixFilterWithholdsNearMissKeys.cs` |
| `events__s3__sqs.xsvc-s3-1-key-suffix-filter-withholds-near-miss-keys` | `4df2823` | ported | `EventsS3Sqs/XsvcS31KeySuffixFilterWithholdsNearMissKeys.cs` |
| `events__s3__sqs.xsvc-s3-1-size-numeric-range-filter-withholds-out-of-range-objects` | `4df2823` | ported | `EventsS3Sqs/XsvcS31SizeNumericRangeFilterWithholdsOutOfRangeObjects.cs` |
| `events__s3__sqs.xsvc-s3-1-versioned-delete-emits-object-deleted-with-deletion-type` | `4df2823` | ported | `EventsS3Sqs/XsvcS31VersionedDeleteEmitsObjectDeletedWithDeletionType.cs` |
