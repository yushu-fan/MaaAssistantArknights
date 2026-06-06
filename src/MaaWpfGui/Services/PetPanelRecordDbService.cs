// <copyright file="PetPanelRecordDbService.cs" company="MaaAssistantArknights">
// Part of the MaaWpfGui project, maintained by the MaaAssistantArknights team (Maa Team)
// Copyright (C) 2021-2026 MaaAssistantArknights Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License v3.0 only as published by
// the Free Software Foundation, either version 3 of the License, or
// any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY
// </copyright>

#nullable enable

using System;
using System.Globalization;
using System.IO;
using MaaWpfGui.Helper;
using MaaWpfGui.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace MaaWpfGui.Services;

public class PetPanelRecordDbService
{
    private static readonly ILogger _logger = Log.ForContext<PetPanelRecordDbService>();
    private readonly string _dbPath;

    public PetPanelRecordDbService(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(PathsHelper.DataDir, "pet_panel_records.db");
        Initialize();
    }

    public long BeginRun(string source, string constraints, string? metadataJson = null)
    {
        const string sql = """
            INSERT INTO runs
            (
                source,
                constraints,
                metadata_json,
                status,
                started_at,
                updated_at
            )
            VALUES
            (
                $source,
                $constraints,
                $metadataJson,
                'running',
                $now,
                $now
            );
            SELECT last_insert_rowid();
            """;

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$constraints", constraints);
            command.Parameters.AddWithValue("$metadataJson", (object?)metadataJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            return (long)(command.ExecuteScalar() ?? 0L);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to create pet panel recording run.");
            return 0;
        }
    }

    public void UpsertPet(long runId, OperBoxData.OperData operData, bool own, bool duplicate)
    {
        const string upsertSql = """
            INSERT INTO pets
            (
                pet_key,
                latest_name,
                latest_level,
                latest_rarity,
                latest_elite,
                latest_potential,
                own,
                latest_run_id,
                version,
                updated_at
            )
            VALUES
            (
                $petKey,
                $name,
                $level,
                $rarity,
                $elite,
                $potential,
                $own,
                $runId,
                1,
                $updatedAt
            )
            ON CONFLICT(pet_key) DO UPDATE SET
                latest_name = excluded.latest_name,
                latest_level = excluded.latest_level,
                latest_rarity = excluded.latest_rarity,
                latest_elite = excluded.latest_elite,
                latest_potential = excluded.latest_potential,
                own = excluded.own,
                latest_run_id = excluded.latest_run_id,
                version = pets.version + 1,
                updated_at = excluded.updated_at;
            """;

        const string snapshotSql = """
            INSERT INTO pet_snapshots
            (
                run_id,
                pet_key,
                panel_name,
                panel_level,
                panel_rarity,
                panel_elite,
                panel_potential,
                raw_payload,
                confidence,
                screenshot_path,
                duplicate,
                created_at
            )
            VALUES
            (
                $runId,
                $petKey,
                $name,
                $level,
                $rarity,
                $elite,
                $potential,
                $rawPayload,
                NULL,
                NULL,
                $duplicate,
                $createdAt
            );
            """;

        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            using var upsertCommand = connection.CreateCommand();
            upsertCommand.Transaction = transaction;
            upsertCommand.CommandText = upsertSql;
            upsertCommand.Parameters.AddWithValue("$petKey", operData.Id);
            upsertCommand.Parameters.AddWithValue("$name", operData.Name);
            upsertCommand.Parameters.AddWithValue("$level", operData.Level);
            upsertCommand.Parameters.AddWithValue("$rarity", operData.Rarity);
            upsertCommand.Parameters.AddWithValue("$elite", operData.Elite);
            upsertCommand.Parameters.AddWithValue("$potential", operData.Potential);
            upsertCommand.Parameters.AddWithValue("$own", own ? 1 : 0);
            upsertCommand.Parameters.AddWithValue("$runId", runId);
            upsertCommand.Parameters.AddWithValue("$updatedAt", now);
            upsertCommand.ExecuteNonQuery();

            using var snapshotCommand = connection.CreateCommand();
            snapshotCommand.Transaction = transaction;
            snapshotCommand.CommandText = snapshotSql;
            snapshotCommand.Parameters.AddWithValue("$runId", runId);
            snapshotCommand.Parameters.AddWithValue("$petKey", operData.Id);
            snapshotCommand.Parameters.AddWithValue("$name", operData.Name);
            snapshotCommand.Parameters.AddWithValue("$level", operData.Level);
            snapshotCommand.Parameters.AddWithValue("$rarity", operData.Rarity);
            snapshotCommand.Parameters.AddWithValue("$elite", operData.Elite);
            snapshotCommand.Parameters.AddWithValue("$potential", operData.Potential);
            snapshotCommand.Parameters.AddWithValue("$rawPayload", "{}");
            snapshotCommand.Parameters.AddWithValue("$duplicate", duplicate ? 1 : 0);
            snapshotCommand.Parameters.AddWithValue("$createdAt", now);
            snapshotCommand.ExecuteNonQuery();

            transaction.Commit();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to upsert pet panel data. RunId={RunId}, PetKey={PetKey}", runId, operData.Id);
        }
    }

    public void LogError(long runId, string stage, string message, string? payloadJson = null, int retryCount = 0)
    {
        const string sql = """
            INSERT INTO errors
            (
                run_id,
                stage,
                message,
                payload_json,
                retry_count,
                created_at
            )
            VALUES
            (
                $runId,
                $stage,
                $message,
                $payloadJson,
                $retryCount,
                $createdAt
            );
            """;

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$stage", stage);
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$payloadJson", (object?)payloadJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$retryCount", retryCount);
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to log pet panel data error. RunId={RunId}, Stage={Stage}", runId, stage);
        }
    }

    public void CompleteRun(long runId, int recognizedCount, int duplicateCount, int failedCount, string? summaryJson = null)
    {
        const string sql = """
            UPDATE runs
            SET
                recognized_count = $recognizedCount,
                duplicate_count = $duplicateCount,
                failed_count = $failedCount,
                summary_json = $summaryJson,
                status = 'completed',
                finished_at = $finishedAt,
                updated_at = $updatedAt
            WHERE id = $runId;
            """;

        TryFinalizeRun(runId, sql, command => {
            var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue("$recognizedCount", recognizedCount);
            command.Parameters.AddWithValue("$duplicateCount", duplicateCount);
            command.Parameters.AddWithValue("$failedCount", failedCount);
            command.Parameters.AddWithValue("$summaryJson", (object?)summaryJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$finishedAt", now);
            command.Parameters.AddWithValue("$updatedAt", now);
        });
    }

    public void AbortRun(long runId, string? summaryJson = null)
    {
        const string sql = """
            UPDATE runs
            SET
                status = 'aborted',
                summary_json = $summaryJson,
                finished_at = $finishedAt,
                updated_at = $updatedAt
            WHERE id = $runId;
            """;

        TryFinalizeRun(runId, sql, command => {
            var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue("$summaryJson", (object?)summaryJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$finishedAt", now);
            command.Parameters.AddWithValue("$updatedAt", now);
        });
    }

    private void TryFinalizeRun(long runId, string sql, Action<SqliteCommand> setupParameters)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$runId", runId);
            setupParameters(command);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to finalize pet panel run. RunId={RunId}", runId);
        }
    }

    private void Initialize()
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS runs
            (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL,
                constraints TEXT NOT NULL,
                metadata_json TEXT NULL,
                recognized_count INTEGER NOT NULL DEFAULT 0,
                duplicate_count INTEGER NOT NULL DEFAULT 0,
                failed_count INTEGER NOT NULL DEFAULT 0,
                summary_json TEXT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS pets
            (
                pet_key TEXT PRIMARY KEY,
                latest_name TEXT NOT NULL,
                latest_level INTEGER NOT NULL,
                latest_rarity INTEGER NOT NULL,
                latest_elite INTEGER NOT NULL,
                latest_potential INTEGER NOT NULL,
                own INTEGER NOT NULL,
                latest_run_id INTEGER NOT NULL,
                version INTEGER NOT NULL DEFAULT 1,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS pet_snapshots
            (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL,
                pet_key TEXT NOT NULL,
                panel_name TEXT NOT NULL,
                panel_level INTEGER NOT NULL,
                panel_rarity INTEGER NOT NULL,
                panel_elite INTEGER NOT NULL,
                panel_potential INTEGER NOT NULL,
                raw_payload TEXT NULL,
                confidence REAL NULL,
                screenshot_path TEXT NULL,
                duplicate INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS errors
            (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL,
                stage TEXT NOT NULL,
                message TEXT NOT NULL,
                payload_json TEXT NULL,
                retry_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            """;

        try
        {
            Directory.CreateDirectory(PathsHelper.DataDir);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = ddl;
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize pet panel database.");
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate;");
        connection.Open();
        return connection;
    }
}
