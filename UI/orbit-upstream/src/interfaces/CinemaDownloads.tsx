// Cinema's Downloads: one download at a time on the stage, the others in the
// strip, and Active, Finished, Failed and Cancelled as collections. The actions
// and dialogs are Downloads' own (see Downloads.tsx and jobs.ts).
import { useEffect, useState, type ReactNode } from "react";
import { bytes, formatLabel, type Game, type Job, type Storage } from "../types";
import { formatNumber, placeName, t } from "../i18n";
import { downloadViews, jobActions, progressOf, statusLabels, type DownloadView, type JobCommand } from "../jobs";
import { CinemaArt, CinemaCollections, CinemaMore, CinemaStage, CinemaStrip, type CinemaFrame } from "./CinemaParts";
import { Glyph, type GlyphName } from "./Glyph";

const commandGlyph: Partial<Record<JobCommand, GlyphName>> = {
  pause: "pause",
  resume: "play",
  retry: "refresh",
  "cancel-dialog": "close",
  remove: "history",
  "refresh-link": "link",
  password: "lock",
};
const emptyText: Record<DownloadView, [string, string]> = {
  Active: [/* i18n */ "Nothing in progress", /* i18n */ "Choose a game to add it to your queue."],
  Failed: [/* i18n */ "No failed downloads", /* i18n */ "Downloads that need your attention appear here."],
  Finished: [/* i18n */ "No finished downloads", /* i18n */ "Successfully completed downloads appear here."],
  Cancelled: [/* i18n */ "No cancelled downloads", /* i18n */ "Cancelled downloads appear here. Kept partial files can be resumed."],
};

export function CinemaDownloads({
  groups,
  queued,
  view,
  setView,
  targetId,
  gameByRelease,
  drives,
  busy,
  error,
  message,
  run,
  cancel,
  password,
  clear,
  browse,
  openLibrary,
  dialogs,
}: {
  jobs: Job[];
  groups: Record<DownloadView, Job[]>;
  queued: Job[];
  view: DownloadView;
  setView: (view: DownloadView) => void;
  targetId: string | null;
  gameByRelease: Map<string, Game>;
  drives: Storage[];
  busy: string;
  error: string;
  message: string;
  run: (job: Job, name: string, remove?: boolean) => Promise<void>;
  cancel: (job: Job) => void;
  password: (job: Job) => void;
  clear: () => void;
  browse: () => void;
  openLibrary?: (job: Job) => void;
  dialogs: ReactNode;
}) {
  const list = groups[view];
  const [chosen, setChosen] = useState<string | null>(targetId);
  useEffect(() => {
    if (targetId) setChosen(targetId);
  }, [targetId]);
  const job = list.find((j) => j.id === chosen) || list[0];
  const index = downloadViews.indexOf(view);
  const step = (delta: number) => {
    const next = downloadViews[index + delta];
    if (!next) return false;
    setView(next);
    setChosen(null);
    return true;
  };
  const collections = (
    <CinemaCollections label={t(view)} index={index} count={downloadViews.length} step={step} />
  );
  if (!job) {
    const [title, body] = emptyText[view];
    return (
      <section className="cn-page cn-downloads">
        {collections}
        {message && (
          <p className="cn-toast" role="status">
            <Glyph name="check" size={16} stroke={2.4} />
            {message}
          </p>
        )}
        <CinemaStage kicker={t(view)} title={t(title)}>
          <p className="cn-body">{t(body)}</p>
          {view === "Active" && (
            <div className="cn-actions">
              <button className="cn-button primary" onClick={browse}>
                {t("Browse games")}
              </button>
            </div>
          )}
        </CinemaStage>
        <div className="cn-cards">
          {downloadViews
            .filter((v) => v !== view && !groups[v].length)
            .map((v) => (
              <div className="cn-card" key={v}>
                <p className="cn-card-kicker">{t(v)}</p>
                <p className="cn-card-title">{t(emptyText[v][0])}</p>
                <p className="cn-card-body">{t(emptyText[v][1])}</p>
              </div>
            ))}
        </div>
        {dialogs}
      </section>
    );
  }
  const game = gameByRelease.get(job.releaseId);
  const release = game?.releases.find((r) => r.id === job.releaseId);
  const title = game?.title || job.title || job.filename;
  const drive = drives.find((d) => d.id === job.storageId);
  const { extracting, done, total, percent } = progressOf(job);
  const seconds = Math.max(0, Math.ceil(job.retryAt - Date.now() / 1000));
  // The gold line says what the stage shows, as the design does for each state.
  const kicker = (
    job.status === "complete"
      ? [t(statusLabels.complete), job.verification === "sha256" ? t("SHA-256 verified") : t("Size verified"), bytes(job.total)]
      : job.status === "cancelled"
        ? [t(statusLabels.cancelled), job.received > 0 ? t("Partial file kept") : "", drive ? placeName(drive.label) : job.storageId]
        : job.status === "error"
          ? [t(statusLabels.error), `${bytes(job.received)} / ${bytes(job.total)}`]
          : [
              t(statusLabels[job.status] || job.status),
              release ? `${release.provider} · ${formatLabel(release)}` : "",
              job.delivery === "torbox" ? t("via TorBox") : "",
            ]
  )
    .filter(Boolean)
    .join(" · ");
  const actions = jobActions(job, { busy: !!busy, drives, queued });
  const doCommand = (command: JobCommand) => {
    if (command === "cancel-dialog") cancel(job);
    else if (command === "password") password(job);
    else void run(job, command);
  };
  // The first two actions are buttons on the stage; the rest wait under More.
  const shown = actions.slice(0, 2);
  const more = actions.slice(2);
  const history = (view === "Finished" || view === "Cancelled") && list.length > 0;
  const frames: CinemaFrame[] = list.map((j) => {
    const g = gameByRelease.get(j.releaseId);
    const p = progressOf(j);
    return {
      key: j.id,
      label: g?.title || j.title || j.filename,
      art: g?.hero || g?.cover,
      fallback: g?.heroFallback || g?.cover,
      badge:
        j.status === "downloading"
          ? `${formatNumber(Math.floor(p.percent))}%`
          : j.delivery === "torbox" && j.status === "queued"
            ? "TorBox"
            : t(statusLabels[j.status] || j.status),
      progress: j.status === "complete" || p.total <= 0 ? undefined : p.percent / 100,
    };
  });
  return (
    <section className="cn-page cn-downloads">
      <CinemaArt src={game?.hero || game?.cover} fallback={game?.heroFallback || game?.cover} />
      {collections}
      {message && (
        <p className="cn-toast" role="status">
          <Glyph name="check" size={16} stroke={2.4} />
          {message}
        </p>
      )}
      <CinemaStage kicker={kicker} title={title}>
        {job.status !== "complete" && job.status !== "cancelled" && job.status !== "error" && (
          <>
            <p className="cn-progress">
              <span className="cn-percent">
                {formatNumber(percent / 100, { style: "percent", maximumFractionDigits: 0 })}
              </span>
              <span>
                {extracting && `${t("Extracting")} · `}
                {bytes(done)} / {bytes(total)}
                {" · "}
                {job.status === "retrying"
                  ? seconds > 0
                    ? t("Retrying in {seconds} s", { seconds })
                    : t("Waiting to retry…")
                  : job.speed > 0
                    ? t("{speed}/s · {minutes} min remaining", {
                        speed: bytes(job.speed),
                        minutes: Math.ceil((total - done) / job.speed / 60),
                      })
                    : t("{size} remaining", { size: bytes(Math.max(0, job.total - job.received)) })}
              </span>
            </p>
            <progress className="cn-bar" max="100" value={percent} aria-label={t("{title} progress", { title })} />
          </>
        )}
        {job.deliveryPhase && ["queued", "downloading"].includes(job.status) && <p className="cn-body">{t(job.deliveryPhase)}</p>}
        {job.error && <p className="cn-body cn-error">{t(job.error)}</p>}
        {job.status === "cancelled" && job.received > 0 && (
          <p className="cn-body">
            {bytes(job.received)} / {bytes(job.total)} ·{" "}
            {t("Partial file kept. Resume it, or delete the partial file before removing this entry.")}
          </p>
        )}
        {job.status === "complete" && job.path && (
          <p className="cn-body">
            {t("Location")} <code dir="ltr">{job.path}</code>
          </p>
        )}
        {error && (
          <p className="cn-body cn-error" role="alert">
            {error}
          </p>
        )}
        <div className="cn-actions">
          {shown.map((a, i) => (
            <button
              key={a.command}
              className={`cn-button ${i === 0 ? "primary" : ""}`}
              disabled={a.disabled}
              onClick={() => doCommand(a.command)}
            >
              {commandGlyph[a.command] && <Glyph name={commandGlyph[a.command]!} size={18} stroke={2} />}
              {t(a.label)}
            </button>
          ))}
          {job.status === "complete" && openLibrary && (
            <button className={`cn-button ${shown.length ? "" : "primary"}`} onClick={() => openLibrary(job)}>
              <Glyph name="books" size={18} />
              {t("View in Library")}
            </button>
          )}
          {history && (
            <button className="cn-button" disabled={!!busy} onClick={clear}>
              {view === "Cancelled" ? t("Clear cancelled history") : t("Clear finished history")}
            </button>
          )}
          <CinemaMore
            actions={more.map((a) => ({ key: a.command, label: t(a.label), disabled: a.disabled, run: () => doCommand(a.command) }))}
          />
        </div>
      </CinemaStage>
      <CinemaStrip
        label={t(view)}
        frames={frames}
        current={job.id}
        choose={setChosen}
        stepCollection={step}
        canStep={(delta) => !!downloadViews[index + delta]}
      />
      {dialogs}
    </section>
  );
}
