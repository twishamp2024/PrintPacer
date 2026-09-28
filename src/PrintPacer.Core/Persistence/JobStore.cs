using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PrintPacer.Core.Billing;
using PrintPacer.Core.Model;
using Serilog;

namespace PrintPacer.Core.Persistence;

/// <summary>
/// Durable storage for jobs, batches, submission attempts and the event log.
///
/// SQLite in WAL mode with synchronous=FULL. That combination matters: the application's
/// whole duplicate-print defence rests on the guarantee that when
/// <see cref="RecordSubmitIntent"/> returns, the intent to print is already on the platter.
/// If we crash one instruction later, the record is still there and recovery knows a job
/// may exist in the queue.
///
/// All access is serialised through one connection behind a lock. The write volume here is
/// a handful of rows per batch, so there is nothing to gain from concurrency and a great
/// deal to lose from a subtle interleaving bug.
/// </summary>
public sealed class JobStore : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<JobStore>();
    private const int SchemaVersion = 4;

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public JobStore(string? databasePath = null)
    {
        var path = databasePath ?? AppPaths.DatabasePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());

        _connection.Open();

        Execute("PRAGMA journal_mode=WAL;");
        // Durability over throughput: a commit must survive a power cut, not merely a crash.
        Execute("PRAGMA synchronous=FULL;");
        Execute("PRAGMA foreign_keys=ON;");
        Execute("PRAGMA busy_timeout=5000;");

        Migrate();
    }

    // ---- Schema ------------------------------------------------------------

    private void Migrate()
    {
        lock (_gate)
        {
            Execute("""
                CREATE TABLE IF NOT EXISTS schema_info (
                    version INTEGER NOT NULL
                );
                """);

            var current = QueryScalarLong("SELECT version FROM schema_info LIMIT 1;");
            if (current is null)
            {
                // A new database is created at v1 and then upgraded like any other, so each
                // schema version has exactly one definition and the two paths cannot drift.
                CreateSchema();
                Execute("INSERT INTO schema_info (version) VALUES (1);");
                current = 1;
                Log.Information("Created job database at {Path}", _connection.DataSource);
            }
            else if (current.Value > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The job database was written by a newer version of {AppPaths.ProductName} " +
                    $"(schema v{current}, this build understands v{SchemaVersion}).");
            }

            if (current < 2) UpgradeToV2();
            if (current < 3) UpgradeToV3();
            if (current < 4) UpgradeToV4();
        }
    }

    /// <summary>
    /// v4: the print queue. <c>queue_position</c> has existed since v1 but was always 0, so the
    /// order jobs were created in becomes their queue order, and each job gets a stable number
    /// ("Job #3") in the same order. States, batches and attempts are untouched: a job that was
    /// half printed before the upgrade carries on exactly where it was.
    /// </summary>
    private void UpgradeToV4()
    {
        using var transaction = _connection.BeginTransaction();
        Execute("ALTER TABLE jobs ADD COLUMN job_number INTEGER NOT NULL DEFAULT 0;", transaction);

        var ids = new List<string>();
        using (var command = Command("SELECT id FROM jobs ORDER BY queue_position, created_utc, id;", transaction))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) ids.Add(reader.GetString(0));
        }

        for (var i = 0; i < ids.Count; i++)
        {
            Execute("UPDATE jobs SET queue_position = $n, job_number = $n WHERE id = $id;", transaction,
                ("$n", (long)(i + 1)), ("$id", ids[i]));
        }

        // Job numbers are never reused, even after finished jobs are cleared from the list, so
        // "Job #12" in last week's log is never a different document from today's.
        Execute("ALTER TABLE schema_info ADD COLUMN next_job_number INTEGER NOT NULL DEFAULT 1;", transaction);
        Execute("UPDATE schema_info SET version = 4, next_job_number = $next;", transaction, ("$next", (long)(ids.Count + 1)));
        transaction.Commit();
        Log.Information("Job database upgraded to schema v4 ({Count} existing job(s) numbered in creation order)", ids.Count);
    }

    /// <summary>
    /// v3: page selection, layout and sides. Every default is what older versions did, so jobs
    /// already in the database - including ones mid-way through printing - carry on unchanged,
    /// and their batch ranges keep meaning source pages.
    /// </summary>
    private void UpgradeToV3()
    {
        using var transaction = _connection.BeginTransaction();
        Execute("""
            ALTER TABLE jobs ADD COLUMN page_selection      INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE jobs ADD COLUMN layout              INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE jobs ADD COLUMN duplex              INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE jobs ADD COLUMN paper               INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE jobs ADD COLUMN margin_mm           TEXT;
            ALTER TABLE jobs ADD COLUMN fold_gap_mm         TEXT;
            ALTER TABLE jobs ADD COLUMN scaling             INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE jobs ADD COLUMN booklet_binding     INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE jobs ADD COLUMN sheets_per_section  INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE jobs ADD COLUMN keep_back_cover     INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE jobs ADD COLUMN brochure_fold       INTEGER NOT NULL DEFAULT 0;
            UPDATE schema_info SET version = 3;
            """, transaction);
        transaction.Commit();
        Log.Information("Job database upgraded to schema v3");
    }

    /// <summary>v2: per-job invoice settings and the per-page colour classification.</summary>
    private void UpgradeToV2()
    {
        using var transaction = _connection.BeginTransaction();
        Execute("""
            ALTER TABLE jobs ADD COLUMN invoice_state       INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE jobs ADD COLUMN bw_page_price       TEXT;
            ALTER TABLE jobs ADD COLUMN colour_page_price   TEXT;
            ALTER TABLE jobs ADD COLUMN page_colour_map     TEXT;
            ALTER TABLE jobs ADD COLUMN invoice_printed_utc TEXT;
            UPDATE schema_info SET version = 2;
            """, transaction);
        transaction.Commit();
        Log.Information("Job database upgraded to schema v2");
    }

    private void CreateSchema()
    {
        Execute("""
            CREATE TABLE jobs (
                id                   TEXT PRIMARY KEY,
                queue_position       INTEGER NOT NULL DEFAULT 0,
                source_path          TEXT NOT NULL,
                file_name            TEXT NOT NULL,
                file_size            INTEGER NOT NULL,
                file_sha256          TEXT NOT NULL,
                total_pages          INTEGER NOT NULL,
                title                TEXT,
                author               TEXT,
                producer             TEXT,
                pdf_version          TEXT,
                is_encrypted         INTEGER NOT NULL DEFAULT 0,
                printer_name         TEXT NOT NULL,
                pages_per_batch      INTEGER NOT NULL,
                delay_minutes        INTEGER NOT NULL,
                start_mode           INTEGER NOT NULL,
                scheduled_start_utc  TEXT,
                render_dpi           INTEGER NOT NULL DEFAULT 300,
                grayscale            INTEGER NOT NULL DEFAULT 0,
                fit_to_printable     INTEGER NOT NULL DEFAULT 1,
                auto_rotate          INTEGER NOT NULL DEFAULT 1,
                state                INTEGER NOT NULL,
                current_batch        INTEGER NOT NULL DEFAULT 1,
                next_batch_due_utc   TEXT,
                created_utc          TEXT NOT NULL,
                updated_utc          TEXT NOT NULL,
                started_utc          TEXT,
                finished_utc         TEXT,
                last_error           TEXT,
                attention_reason     TEXT,
                last_printer_status  TEXT,
                consecutive_blocked  INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE batches (
                row_id           INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id           TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                number           INTEGER NOT NULL,
                first_page       INTEGER NOT NULL,
                last_page        INTEGER NOT NULL,
                state            INTEGER NOT NULL,
                submitted_utc    TEXT,
                finished_utc     TEXT,
                pages_confirmed  INTEGER NOT NULL DEFAULT 0,
                note             TEXT,
                UNIQUE (job_id, number)
            );

            CREATE TABLE attempts (
                row_id                INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id                TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                batch_number          INTEGER NOT NULL,
                attempt_number        INTEGER NOT NULL,
                document_token        TEXT NOT NULL UNIQUE,
                state                 INTEGER NOT NULL,
                spooler_job_id        INTEGER,
                printer_name          TEXT NOT NULL,
                intent_utc            TEXT NOT NULL,
                submitted_utc         TEXT,
                finished_utc          TEXT,
                spooler_pages_printed INTEGER NOT NULL DEFAULT 0,
                observed_in_queue     INTEGER NOT NULL DEFAULT 0,
                observed_printing     INTEGER NOT NULL DEFAULT 0,
                dev_impr_before       INTEGER,
                dev_impr_after        INTEGER,
                dev_jam_before        INTEGER,
                dev_jam_after         INTEGER,
                dev_mispick_before    INTEGER,
                dev_mispick_after     INTEGER,
                evidence              INTEGER NOT NULL DEFAULT 0,
                last_spooler_status   TEXT,
                error                 TEXT,
                verdict_explanation   TEXT,
                UNIQUE (job_id, batch_number, attempt_number)
            );

            CREATE TABLE events (
                row_id    INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id    TEXT,
                utc       TEXT NOT NULL,
                level     TEXT NOT NULL,
                category  TEXT NOT NULL,
                message   TEXT NOT NULL,
                detail    TEXT
            );

            CREATE INDEX idx_batches_job     ON batches (job_id, number);
            CREATE INDEX idx_attempts_job    ON attempts (job_id, batch_number, attempt_number);
            CREATE INDEX idx_attempts_state  ON attempts (state);
            CREATE INDEX idx_events_job      ON events (job_id, row_id);
            CREATE INDEX idx_jobs_state      ON jobs (state);
            """);
    }

    // ---- Jobs --------------------------------------------------------------

    public void InsertJob(PrintJob job)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            Execute("""
                INSERT INTO jobs (
                    id, job_number, queue_position, source_path, file_name, file_size, file_sha256, total_pages,
                    title, author, producer, pdf_version, is_encrypted,
                    printer_name, pages_per_batch, delay_minutes, start_mode, scheduled_start_utc,
                    render_dpi, grayscale, fit_to_printable, auto_rotate,
                    state, current_batch, next_batch_due_utc,
                    created_utc, updated_utc, started_utc, finished_utc,
                    last_error, attention_reason, last_printer_status, consecutive_blocked,
                    invoice_state, bw_page_price, colour_page_price, page_colour_map, invoice_printed_utc,
                    page_selection, layout, duplex, paper, margin_mm, fold_gap_mm, scaling,
                    booklet_binding, sheets_per_section, keep_back_cover, brochure_fold
                ) VALUES (
                    $id, $number, $pos, $path, $name, $size, $sha, $pages,
                    $title, $author, $producer, $ver, $enc,
                    $printer, $ppb, $delay, $mode, $start,
                    $dpi, $gray, $fit, $rot,
                    $state, $cur, $due,
                    $created, $updated, $started, $finished,
                    $err, $att, $pstatus, $blocked,
                    $inv, $bwprice, $colprice, $colmap, $invprinted,
                    $psel, $layout, $duplex, $paper, $margin, $foldgap, $scaling,
                    $binding, $section, $backcover, $fold
                );
                """,
                transaction,
                ("$id", job.Id), ("$number", (long)job.Number), ("$pos", (long)job.QueuePosition),
                ("$path", job.SourcePath), ("$name", job.FileName),
                ("$size", job.FileSizeBytes), ("$sha", job.FileSha256), ("$pages", (long)job.TotalPages),
                ("$title", job.Title), ("$author", job.Author), ("$producer", job.Producer),
                ("$ver", job.PdfVersion), ("$enc", job.IsEncrypted ? 1L : 0L),
                ("$printer", job.PrinterName), ("$ppb", (long)job.PagesPerBatch),
                ("$delay", (long)job.DelayMinutes), ("$mode", (long)job.StartMode),
                ("$start", Time(job.ScheduledStartUtc)),
                ("$dpi", (long)job.RenderDpi), ("$gray", (long)job.ColourMode),
                ("$fit", job.FitToPrintableArea ? 1L : 0L), ("$rot", job.AutoRotate ? 1L : 0L),
                ("$state", (long)job.State), ("$cur", (long)job.CurrentBatchNumber),
                ("$due", Time(job.NextBatchDueUtc)),
                ("$created", Time(job.CreatedUtc)), ("$updated", Time(job.UpdatedUtc)),
                ("$started", Time(job.StartedUtc)), ("$finished", Time(job.FinishedUtc)),
                ("$err", job.LastError), ("$att", job.AttentionReason),
                ("$pstatus", job.LastPrinterStatus), ("$blocked", (long)job.ConsecutiveBlockedChecks),
                ("$inv", (long)job.InvoiceState), ("$bwprice", Money(job.BlackWhitePagePrice)),
                ("$colprice", Money(job.ColourPagePrice)), ("$colmap", job.PageColourMap),
                ("$invprinted", Time(job.InvoicePrintedUtc)),
                ("$psel", (long)job.PrintSettings.PageSelection), ("$layout", (long)job.PrintSettings.Layout),
                ("$duplex", (long)job.PrintSettings.Duplex), ("$paper", (long)job.PrintSettings.Paper),
                ("$margin", Millimetres(job.PrintSettings.MarginMm)), ("$foldgap", Millimetres(job.PrintSettings.FoldGapMm)),
                ("$scaling", (long)job.PrintSettings.Scaling), ("$binding", (long)job.PrintSettings.Binding),
                ("$section", (long)job.PrintSettings.SheetsPerSignature),
                ("$backcover", job.PrintSettings.KeepLastPageAsBackCover ? 1L : 0L),
                ("$fold", (long)job.PrintSettings.Fold));

            InsertBatches(job, transaction);
            transaction.Commit();
        }
    }

    private void InsertBatches(PrintJob job, SqliteTransaction transaction)
    {
        foreach (var batch in job.Batches)
        {
            Execute("""
                INSERT INTO batches (job_id, number, first_page, last_page, state,
                                     submitted_utc, finished_utc, pages_confirmed, note)
                VALUES ($job, $num, $first, $last, $state, $sub, $fin, $conf, $note);
                """,
                transaction,
                ("$job", batch.JobId), ("$num", (long)batch.Number),
                ("$first", (long)batch.FirstPage), ("$last", (long)batch.LastPage),
                ("$state", (long)batch.State), ("$sub", Time(batch.SubmittedUtc)),
                ("$fin", Time(batch.FinishedUtc)), ("$conf", (long)batch.PagesConfirmedPrinted),
                ("$note", batch.Note));
        }
    }

    /// <summary>
    /// Stores a new configuration for a job that has not started, with the batches planned from
    /// it, in one transaction: a crash leaves either the old settings and batches or the new ones.
    ///
    /// Refuses a job with any submission attempt on record. Re-planning its batches would detach
    /// that history from the pages it printed, which is exactly the record recovery depends on.
    /// </summary>
    public void ReplaceConfiguration(PrintJob job)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            var attempts = QueryScalarLong("SELECT COUNT(*) FROM attempts WHERE job_id = $id;", transaction, ("$id", job.Id));
            if (attempts > 0)
                throw new InvalidOperationException($"{job.Label} has already sent pages to the printer; its settings cannot change.");

            job.UpdatedUtc = DateTimeOffset.UtcNow;
            Execute("""
                UPDATE jobs SET
                    printer_name = $printer, pages_per_batch = $ppb, delay_minutes = $delay,
                    start_mode = $mode, scheduled_start_utc = $start, render_dpi = $dpi, grayscale = $gray,
                    invoice_state = $inv, bw_page_price = $bwprice, colour_page_price = $colprice,
                    page_colour_map = $colmap,
                    page_selection = $psel, layout = $layout, duplex = $duplex, paper = $paper,
                    margin_mm = $margin, fold_gap_mm = $foldgap, scaling = $scaling,
                    booklet_binding = $binding, sheets_per_section = $section,
                    keep_back_cover = $backcover, brochure_fold = $fold,
                    state = $state, updated_utc = $updated
                WHERE id = $id;
                """,
                transaction,
                ("$printer", job.PrinterName), ("$ppb", (long)job.PagesPerBatch),
                ("$delay", (long)job.DelayMinutes), ("$mode", (long)job.StartMode),
                ("$start", Time(job.ScheduledStartUtc)), ("$dpi", (long)job.RenderDpi),
                ("$gray", (long)job.ColourMode),
                ("$inv", (long)job.InvoiceState), ("$bwprice", Money(job.BlackWhitePagePrice)),
                ("$colprice", Money(job.ColourPagePrice)), ("$colmap", job.PageColourMap),
                ("$psel", (long)job.PrintSettings.PageSelection), ("$layout", (long)job.PrintSettings.Layout),
                ("$duplex", (long)job.PrintSettings.Duplex), ("$paper", (long)job.PrintSettings.Paper),
                ("$margin", Millimetres(job.PrintSettings.MarginMm)), ("$foldgap", Millimetres(job.PrintSettings.FoldGapMm)),
                ("$scaling", (long)job.PrintSettings.Scaling), ("$binding", (long)job.PrintSettings.Binding),
                ("$section", (long)job.PrintSettings.SheetsPerSignature),
                ("$backcover", job.PrintSettings.KeepLastPageAsBackCover ? 1L : 0L),
                ("$fold", (long)job.PrintSettings.Fold),
                ("$state", (long)job.State), ("$updated", Time(job.UpdatedUtc)),
                ("$id", job.Id));

            Execute("DELETE FROM batches WHERE job_id = $id;", transaction, ("$id", job.Id));
            InsertBatches(job, transaction);
            transaction.Commit();
        }
    }

    /// <summary>The next job number, which is then never handed out again.</summary>
    public int AllocateJobNumber()
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var next = QueryScalarLong("SELECT next_job_number FROM schema_info LIMIT 1;", transaction) ?? 1;
            var used = QueryScalarLong("SELECT COALESCE(MAX(job_number), 0) FROM jobs;", transaction) ?? 0;
            var number = Math.Max(next, used + 1);
            Execute("UPDATE schema_info SET next_job_number = $n;", transaction, ("$n", number + 1));
            transaction.Commit();
            return (int)number;
        }
    }

    /// <summary>Stores the queue order - every job's position - in one transaction.</summary>
    public void SaveQueueOrder(IEnumerable<PrintJob> jobs)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var job in jobs)
            {
                Execute("UPDATE jobs SET queue_position = $pos WHERE id = $id;", transaction,
                    ("$pos", (long)job.QueuePosition), ("$id", job.Id));
            }
            transaction.Commit();
        }
    }

    public void UpdateJob(PrintJob job)
    {
        lock (_gate)
        {
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            Execute("""
                UPDATE jobs SET
                    source_path = $path, printer_name = $printer, state = $state,
                    current_batch = $cur, next_batch_due_utc = $due,
                    scheduled_start_utc = $start,
                    updated_utc = $updated, started_utc = $started, finished_utc = $finished,
                    last_error = $err, attention_reason = $att, last_printer_status = $pstatus,
                    consecutive_blocked = $blocked,
                    invoice_state = $inv, invoice_printed_utc = $invprinted
                WHERE id = $id;
                """,
                null,
                ("$path", job.SourcePath), ("$printer", job.PrinterName), ("$state", (long)job.State),
                ("$cur", (long)job.CurrentBatchNumber), ("$due", Time(job.NextBatchDueUtc)),
                ("$start", Time(job.ScheduledStartUtc)),
                ("$updated", Time(job.UpdatedUtc)), ("$started", Time(job.StartedUtc)),
                ("$finished", Time(job.FinishedUtc)),
                ("$err", job.LastError), ("$att", job.AttentionReason),
                ("$pstatus", job.LastPrinterStatus), ("$blocked", (long)job.ConsecutiveBlockedChecks),
                ("$inv", (long)job.InvoiceState), ("$invprinted", Time(job.InvoicePrintedUtc)),
                ("$id", job.Id));
        }
    }

    /// <summary>
    /// Adds a batch to a job that is already running. Used when a partially printed batch
    /// is split, so that the pages that did not come out become their own unit of work.
    /// </summary>
    public void InsertBatchForExistingJob(Batch batch)
    {
        lock (_gate)
        {
            Execute("""
                INSERT INTO batches (job_id, number, first_page, last_page, state,
                                     submitted_utc, finished_utc, pages_confirmed, note)
                VALUES ($job, $num, $first, $last, $state, $sub, $fin, $conf, $note);
                """,
                null,
                ("$job", batch.JobId), ("$num", (long)batch.Number),
                ("$first", (long)batch.FirstPage), ("$last", (long)batch.LastPage),
                ("$state", (long)batch.State), ("$sub", Time(batch.SubmittedUtc)),
                ("$fin", Time(batch.FinishedUtc)), ("$conf", (long)batch.PagesConfirmedPrinted),
                ("$note", batch.Note));

            batch.RowId = QueryScalarLong("SELECT last_insert_rowid();") ?? 0;
        }
    }

    public void UpdateBatch(Batch batch)
    {
        lock (_gate)
        {
            Execute("""
                UPDATE batches SET state = $state, submitted_utc = $sub, finished_utc = $fin,
                                   pages_confirmed = $conf, note = $note
                WHERE job_id = $job AND number = $num;
                """,
                null,
                ("$state", (long)batch.State), ("$sub", Time(batch.SubmittedUtc)),
                ("$fin", Time(batch.FinishedUtc)), ("$conf", (long)batch.PagesConfirmedPrinted),
                ("$note", batch.Note), ("$job", batch.JobId), ("$num", (long)batch.Number));
        }
    }

    public void DeleteJob(string jobId)
    {
        lock (_gate) Execute("DELETE FROM jobs WHERE id = $id;", null, ("$id", jobId));
    }

    public IReadOnlyList<PrintJob> LoadAllJobs()
    {
        lock (_gate)
        {
            var jobs = new List<PrintJob>();
            using (var command = Command("SELECT * FROM jobs ORDER BY queue_position, created_utc, id;", null))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) jobs.Add(ReadJob(reader));
            }

            foreach (var job in jobs)
            {
                using (var command = Command(
                    "SELECT * FROM batches WHERE job_id = $id ORDER BY number;", null, ("$id", job.Id)))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read()) job.Batches.Add(ReadBatch(reader));
                }

                using (var command = Command(
                    "SELECT * FROM attempts WHERE job_id = $id ORDER BY batch_number, attempt_number;",
                    null, ("$id", job.Id)))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var attempt = ReadAttempt(reader);
                        job.Batches.FirstOrDefault(b => b.Number == attempt.BatchNumber)?.Attempts.Add(attempt);
                    }
                }

                try
                {
                    job.AttachPlanToBatches();
                }
                catch (Exception ex)
                {
                    // Loading must never fail because of one job: the engine refuses to print a
                    // job whose settings make no plan, and says so on screen.
                    Log.Error(ex, "Job {Job} has print settings that do not make a print plan", job.Id);
                }
            }

            return jobs;
        }
    }

    // ---- Attempts ----------------------------------------------------------

    /// <summary>
    /// Records the intention to submit a batch, and does not return until that record is
    /// durably on disk.
    ///
    /// This call is the hinge of the entire crash-safety design. It must happen strictly
    /// before the print API is invoked, so that there is no window in which a job can exist
    /// in the Windows queue that we have no record of.
    /// </summary>
    public BatchAttempt RecordSubmitIntent(PrintJob job, Batch batch, DeviceCounters? counters)
    {
        lock (_gate)
        {
            var attemptNumber = (int)(QueryScalarLong(
                "SELECT COALESCE(MAX(attempt_number), 0) FROM attempts WHERE job_id = $j AND batch_number = $b;",
                null, ("$j", job.Id), ("$b", (long)batch.Number)) ?? 0) + 1;

            var attempt = new BatchAttempt
            {
                JobId = job.Id,
                BatchNumber = batch.Number,
                AttemptNumber = attemptNumber,
                DocumentToken = BuildDocumentToken(job, batch, attemptNumber),
                PrinterName = job.PrinterName,
                IntentUtc = DateTimeOffset.UtcNow,
                State = AttemptState.SubmitPending,
                DeviceImpressionsBefore = counters?.Impressions,
                DeviceJamEventsBefore = counters?.JamEvents,
                DeviceMispickEventsBefore = counters?.MispickEvents,
            };

            using var transaction = _connection.BeginTransaction();

            Execute("""
                INSERT INTO attempts (job_id, batch_number, attempt_number, document_token, state,
                                      printer_name, intent_utc,
                                      dev_impr_before, dev_jam_before, dev_mispick_before)
                VALUES ($job, $batch, $attempt, $token, $state, $printer, $intent,
                        $impr, $jam, $mispick);
                """,
                transaction,
                ("$job", attempt.JobId), ("$batch", (long)attempt.BatchNumber),
                ("$attempt", (long)attempt.AttemptNumber), ("$token", attempt.DocumentToken),
                ("$state", (long)attempt.State), ("$printer", attempt.PrinterName),
                ("$intent", Time(attempt.IntentUtc)),
                ("$impr", attempt.DeviceImpressionsBefore),
                ("$jam", attempt.DeviceJamEventsBefore),
                ("$mispick", attempt.DeviceMispickEventsBefore));

            attempt.RowId = QueryScalarLong("SELECT last_insert_rowid();", transaction) ?? 0;
            transaction.Commit();

            batch.Attempts.Add(attempt);
            return attempt;
        }
    }

    public void UpdateAttempt(BatchAttempt attempt)
    {
        lock (_gate)
        {
            Execute("""
                UPDATE attempts SET
                    state = $state, spooler_job_id = $jobid,
                    submitted_utc = $sub, finished_utc = $fin,
                    spooler_pages_printed = $pages,
                    observed_in_queue = $inq, observed_printing = $printing,
                    dev_impr_before = $ib, dev_impr_after = $ia,
                    dev_jam_before = $jb, dev_jam_after = $ja,
                    dev_mispick_before = $mb, dev_mispick_after = $ma,
                    evidence = $ev, last_spooler_status = $status,
                    error = $err, verdict_explanation = $verdict
                WHERE row_id = $row;
                """,
                null,
                ("$state", (long)attempt.State),
                ("$jobid", attempt.SpoolerJobId is { } id ? (long)id : null),
                ("$sub", Time(attempt.SubmittedUtc)), ("$fin", Time(attempt.FinishedUtc)),
                ("$pages", (long)attempt.SpoolerPagesPrinted),
                ("$inq", attempt.ObservedInQueue ? 1L : 0L),
                ("$printing", attempt.ObservedPrinting ? 1L : 0L),
                ("$ib", attempt.DeviceImpressionsBefore), ("$ia", attempt.DeviceImpressionsAfter),
                ("$jb", attempt.DeviceJamEventsBefore), ("$ja", attempt.DeviceJamEventsAfter),
                ("$mb", attempt.DeviceMispickEventsBefore), ("$ma", attempt.DeviceMispickEventsAfter),
                ("$ev", (long)attempt.Evidence), ("$status", attempt.LastSpoolerStatus),
                ("$err", attempt.Error), ("$verdict", attempt.VerdictExplanation),
                ("$row", attempt.RowId));
        }
    }

    /// <summary>
    /// Builds the spooler document name. It carries enough context to be meaningful in the
    /// Windows print queue UI, and ends with a unique token so that recovery can find
    /// exactly this attempt and never confuse it with a retry or another job.
    /// </summary>
    private static string BuildDocumentToken(PrintJob job, Batch batch, int attemptNumber)
    {
        var stem = Path.GetFileNameWithoutExtension(job.FileName);
        if (stem.Length > 40) stem = stem[..40];

        var unique = Guid.NewGuid().ToString("N")[..12];
        return $"{stem} [{batch.FirstPage}-{batch.LastPage}] " +
               $"PrintPacer#{job.Id[..8]}.{batch.Number}.{attemptNumber}.{unique}";
    }

    /// <summary>Recognises a document name this application created, for any job.</summary>
    public static bool IsOurDocument(string? documentName) =>
        documentName is not null && documentName.Contains("PrintPacer#", StringComparison.Ordinal);

    // ---- Event log ---------------------------------------------------------

    public void AppendEvent(string? jobId, string level, string category, string message, string? detail = null)
    {
        lock (_gate)
        {
            Execute("""
                INSERT INTO events (job_id, utc, level, category, message, detail)
                VALUES ($job, $utc, $level, $cat, $msg, $detail);
                """,
                null,
                ("$job", jobId), ("$utc", Time(DateTimeOffset.UtcNow)),
                ("$level", level), ("$cat", category), ("$msg", message), ("$detail", detail));
        }
    }

    public IReadOnlyList<JobEvent> LoadEvents(string? jobId, int limit = 500)
    {
        lock (_gate)
        {
            var sql = jobId is null
                ? "SELECT * FROM events ORDER BY row_id DESC LIMIT $limit;"
                : "SELECT * FROM events WHERE job_id = $job ORDER BY row_id DESC LIMIT $limit;";

            var parameters = jobId is null
                ? new (string, object?)[] { ("$limit", (long)limit) }
                : new (string, object?)[] { ("$job", jobId), ("$limit", (long)limit) };

            var events = new List<JobEvent>();
            using var command = Command(sql, null, parameters);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new JobEvent(
                    reader.GetInt64(reader.GetOrdinal("row_id")),
                    GetString(reader, "job_id"),
                    ParseTime(GetString(reader, "utc")) ?? DateTimeOffset.UnixEpoch,
                    GetString(reader, "level") ?? "Information",
                    GetString(reader, "category") ?? "",
                    GetString(reader, "message") ?? "",
                    GetString(reader, "detail")));
            }

            events.Reverse();
            return events;
        }
    }

    public void TrimEvents(int keepPerJob = 5000)
    {
        lock (_gate)
        {
            Execute("""
                DELETE FROM events WHERE row_id NOT IN (
                    SELECT row_id FROM events ORDER BY row_id DESC LIMIT $keep
                );
                """, null, ("$keep", (long)keepPerJob));
        }
    }

    // ---- Row mapping -------------------------------------------------------

    private static PrintJob ReadJob(IDataRecord r) => new()
    {
        Id = GetString(r, "id")!,
        Number = (int)(GetLong(r, "job_number") ?? 0),
        QueuePosition = (int)(GetLong(r, "queue_position") ?? 0),
        SourcePath = GetString(r, "source_path")!,
        FileName = GetString(r, "file_name")!,
        FileSizeBytes = GetLong(r, "file_size") ?? 0,
        FileSha256 = GetString(r, "file_sha256")!,
        TotalPages = (int)(GetLong(r, "total_pages") ?? 0),
        Title = GetString(r, "title"),
        Author = GetString(r, "author"),
        Producer = GetString(r, "producer"),
        PdfVersion = GetString(r, "pdf_version"),
        IsEncrypted = (GetLong(r, "is_encrypted") ?? 0) != 0,
        PrinterName = GetString(r, "printer_name")!,
        PagesPerBatch = (int)(GetLong(r, "pages_per_batch") ?? 1),
        DelayMinutes = (int)(GetLong(r, "delay_minutes") ?? 0),
        StartMode = (StartMode)(GetLong(r, "start_mode") ?? 0),
        ScheduledStartUtc = ParseTime(GetString(r, "scheduled_start_utc")),
        RenderDpi = (int)(GetLong(r, "render_dpi") ?? 300),
        // The column predates ColourMode and is named for the greyscale flag it used to hold;
        // its 0/1 are the enum's values. Anything unexpected prints in colour, the default.
        ColourMode = GetLong(r, "grayscale") == (long)ColourMode.BlackAndWhite
            ? ColourMode.BlackAndWhite
            : ColourMode.Colour,
        FitToPrintableArea = (GetLong(r, "fit_to_printable") ?? 1) != 0,
        AutoRotate = (GetLong(r, "auto_rotate") ?? 1) != 0,
        State = (JobState)(GetLong(r, "state") ?? 0),
        CurrentBatchNumber = (int)(GetLong(r, "current_batch") ?? 1),
        NextBatchDueUtc = ParseTime(GetString(r, "next_batch_due_utc")),
        CreatedUtc = ParseTime(GetString(r, "created_utc")) ?? DateTimeOffset.UtcNow,
        UpdatedUtc = ParseTime(GetString(r, "updated_utc")) ?? DateTimeOffset.UtcNow,
        StartedUtc = ParseTime(GetString(r, "started_utc")),
        FinishedUtc = ParseTime(GetString(r, "finished_utc")),
        LastError = GetString(r, "last_error"),
        AttentionReason = GetString(r, "attention_reason"),
        LastPrinterStatus = GetString(r, "last_printer_status"),
        ConsecutiveBlockedChecks = (int)(GetLong(r, "consecutive_blocked") ?? 0),
        InvoiceState = (InvoiceState)(GetLong(r, "invoice_state") ?? 0),
        BlackWhitePagePrice = ParseMoney(GetString(r, "bw_page_price")),
        ColourPagePrice = ParseMoney(GetString(r, "colour_page_price")),
        PageColourMap = GetString(r, "page_colour_map"),
        InvoicePrintedUtc = ParseTime(GetString(r, "invoice_printed_utc")),
        PrintSettings = ReadPrintSettings(r),
    };

    /// <summary>
    /// A job's print settings. A value no version wrote - a hand-edited or damaged row - falls
    /// back to that setting's default rather than being guessed at.
    /// </summary>
    private static PrintSettings ReadPrintSettings(IDataRecord r)
    {
        var defaults = PrintSettings.Default;
        return new PrintSettings
        {
            PageSelection = Defined(GetLong(r, "page_selection"), defaults.PageSelection),
            Layout = Defined(GetLong(r, "layout"), defaults.Layout),
            Duplex = Defined(GetLong(r, "duplex"), defaults.Duplex),
            Paper = Defined(GetLong(r, "paper"), defaults.Paper),
            MarginMm = ParseMillimetres(GetString(r, "margin_mm")) ?? defaults.MarginMm,
            FoldGapMm = ParseMillimetres(GetString(r, "fold_gap_mm")) ?? defaults.FoldGapMm,
            Scaling = Defined(GetLong(r, "scaling"), defaults.Scaling),
            Binding = Defined(GetLong(r, "booklet_binding"), defaults.Binding),
            SheetsPerSignature = (int)Math.Clamp(GetLong(r, "sheets_per_section") ?? 0, 0, PrintSettings.MaxSheetsPerSignature),
            KeepLastPageAsBackCover = (GetLong(r, "keep_back_cover") ?? 0) != 0,
            Fold = Defined(GetLong(r, "brochure_fold"), defaults.Fold),
        };
    }

    private static T Defined<T>(long? value, T fallback) where T : struct, Enum
    {
        if (value is null || value < int.MinValue || value > int.MaxValue) return fallback;
        var candidate = (T)Enum.ToObject(typeof(T), (int)value.Value);
        return Enum.IsDefined(candidate) ? candidate : fallback;
    }

    // Millimetres as invariant text, like money: a REAL column would round-trip 6.35 as 6.3499999.
    private static string Millimetres(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static double? ParseMillimetres(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && double.IsFinite(mm)
            ? mm
            : null;

    private static Batch ReadBatch(IDataRecord r) => new()
    {
        RowId = GetLong(r, "row_id") ?? 0,
        JobId = GetString(r, "job_id")!,
        Number = (int)(GetLong(r, "number") ?? 0),
        FirstPage = (int)(GetLong(r, "first_page") ?? 0),
        LastPage = (int)(GetLong(r, "last_page") ?? 0),
        State = (BatchState)(GetLong(r, "state") ?? 0),
        SubmittedUtc = ParseTime(GetString(r, "submitted_utc")),
        FinishedUtc = ParseTime(GetString(r, "finished_utc")),
        PagesConfirmedPrinted = (int)(GetLong(r, "pages_confirmed") ?? 0),
        Note = GetString(r, "note"),
    };

    private static BatchAttempt ReadAttempt(IDataRecord r) => new()
    {
        RowId = GetLong(r, "row_id") ?? 0,
        JobId = GetString(r, "job_id")!,
        BatchNumber = (int)(GetLong(r, "batch_number") ?? 0),
        AttemptNumber = (int)(GetLong(r, "attempt_number") ?? 0),
        DocumentToken = GetString(r, "document_token")!,
        State = (AttemptState)(GetLong(r, "state") ?? 0),
        SpoolerJobId = GetLong(r, "spooler_job_id") is { } id ? (int)id : null,
        PrinterName = GetString(r, "printer_name")!,
        IntentUtc = ParseTime(GetString(r, "intent_utc")) ?? DateTimeOffset.UnixEpoch,
        SubmittedUtc = ParseTime(GetString(r, "submitted_utc")),
        FinishedUtc = ParseTime(GetString(r, "finished_utc")),
        SpoolerPagesPrinted = (int)(GetLong(r, "spooler_pages_printed") ?? 0),
        ObservedInQueue = (GetLong(r, "observed_in_queue") ?? 0) != 0,
        ObservedPrinting = (GetLong(r, "observed_printing") ?? 0) != 0,
        DeviceImpressionsBefore = GetLong(r, "dev_impr_before"),
        DeviceImpressionsAfter = GetLong(r, "dev_impr_after"),
        DeviceJamEventsBefore = GetLong(r, "dev_jam_before"),
        DeviceJamEventsAfter = GetLong(r, "dev_jam_after"),
        DeviceMispickEventsBefore = GetLong(r, "dev_mispick_before"),
        DeviceMispickEventsAfter = GetLong(r, "dev_mispick_after"),
        Evidence = (CompletionEvidence)(GetLong(r, "evidence") ?? 0),
        LastSpoolerStatus = GetString(r, "last_spooler_status"),
        Error = GetString(r, "error"),
        VerdictExplanation = GetString(r, "verdict_explanation"),
    };

    // ---- Plumbing ----------------------------------------------------------

    private static string? GetString(IDataRecord r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static long? GetLong(IDataRecord r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    /// <summary>Round-trip UTC format, sortable as text, unambiguous across DST and time zones.</summary>
    private static string? Time(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTime(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;

    /// <summary>
    /// Prices are stored as invariant text: SQLite's REAL is a binary double, and a bill
    /// that is a fraction of a cent out is still a wrong bill.
    /// </summary>
    private static string Money(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal ParseMoney(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;

    private SqliteCommand Command(string sql, SqliteTransaction? transaction,
        params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null) command.Transaction = transaction;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void Execute(string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        command.ExecuteNonQuery();
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters) =>
        Execute(sql, null, parameters);

    private long? QueryScalarLong(string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public void Checkpoint()
    {
        lock (_gate) Execute("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { Execute("PRAGMA wal_checkpoint(TRUNCATE);"); }
            catch (Exception ex) { Log.Debug(ex, "WAL checkpoint on shutdown failed"); }
            _connection.Dispose();
        }
    }
}

/// <summary>Physical counters captured from the device around a submission.</summary>
public readonly record struct DeviceCounters(long? Impressions, long? JamEvents, long? MispickEvents);

public sealed record JobEvent(
    long RowId,
    string? JobId,
    DateTimeOffset Utc,
    string Level,
    string Category,
    string Message,
    string? Detail);
