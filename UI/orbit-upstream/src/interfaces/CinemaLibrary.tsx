// Cinema's Library: a game (or a drive) on the stage, the rest in the strip,
// and the views plus Storage as collections. Actions, confirmations and the
// scan are Library's own (Library.tsx, LibraryActions.tsx).
import { useState, type ReactNode } from "react";
import { bytes, type LibraryGame, type Storage } from "../types";
import type { LibraryModel } from "../useLibrary";
import { formatDate, placeName, t, tn } from "../i18n";
import { GameActions, gamesOnDrive, LibraryFeedback, StorageProgress } from "../LibraryActions";
import { Select } from "../Select";
import { Icon } from "../components";
import { CinemaArt, CinemaCollections, CinemaStage, CinemaStrip, type CinemaFrame } from "./CinemaParts";
import { Glyph } from "./Glyph";

const statusOf = (g: LibraryGame) =>
  !g.onDrive ? t("Unavailable") : g.mounted ? t("Mounted") : g.installed ? t("Installed") : t("On drive");

export function CinemaLibrary({
  model,
  games,
  rows,
  views,
  view,
  setView,
  section,
  setSection,
  query,
  setQuery,
  location,
  setLocation,
  selected,
  setSelected,
  ready,
  busy,
  stale,
  error,
  lookup,
  dismissLookup,
  openScan,
  reset,
  dialogs,
  drives,
}: {
  model: LibraryModel;
  games: LibraryGame[];
  rows: LibraryGame[];
  views: readonly string[];
  view: string;
  setView: (view: string) => void;
  section: "games" | "storage";
  setSection: (section: "games" | "storage") => void;
  query: string;
  setQuery: (value: string) => void;
  location: string;
  setLocation: (value: string) => void;
  selected: string | null;
  setSelected: (titleId: string | null) => void;
  ready: boolean;
  busy: boolean;
  stale: boolean;
  error: string;
  lookup: { title: string } | null;
  dismissLookup: () => void;
  openScan: () => void;
  reset: () => void;
  dialogs: ReactNode;
  drives: Storage[];
}) {
  const [details, setDetails] = useState(false);
  const [drivePick, setDrivePick] = useState("");
  // The collections: each view, then Storage.
  const names = [...views, /* i18n */ "Storage"];
  const index = section === "storage" ? views.length : Math.max(0, views.indexOf(view));
  const step = (delta: number) => {
    const next = index + delta;
    if (next < 0 || next >= names.length) return false;
    if (next === views.length) setSection("storage");
    else {
      setSection("games");
      setView(views[next]);
    }
    return true;
  };
  const collections = <CinemaCollections label={t(names[index])} index={index} count={names.length} step={step} />;
  const refresh = (
    <button className="cn-button" onClick={() => void model.read(true)} disabled={busy || Date.now() < model.refreshAt}>
      <Glyph name="refresh" size={18} />
      {busy ? t("Refreshing…") : t("Refresh library")}
    </button>
  );
  const lookupToast = lookup && (
    <div className="cn-toast cn-toast-wide" role="status">
      <Glyph name="search" size={16} stroke={2.4} />
      <div>
        <strong>{t("Looking for {title}", { title: lookup.title })}</strong>
        <p>
          {t(
            "The completed file is not in the current Library inventory. Check that its drive is connected, then scan for games. A completed download alone does not confirm installation.",
          )}
        </p>
        <div className="cn-actions">
          {model.can("rescan") && (
            <button className="cn-button primary" onClick={openScan}>
              {t("Scan for games")}
            </button>
          )}
          <button className="cn-button" onClick={dismissLookup}>
            {t("Dismiss")}
          </button>
        </div>
      </div>
    </div>
  );
  if (!ready) {
    return (
      <section className="cn-page cn-library">
        <CinemaArt />
        {collections}
        {lookupToast}
        <CinemaStage
          kicker={busy ? t("Reading your library") : t("ShadowMount isn’t answering")}
          title={busy ? t("Finding your games") : t("Connect your library")}
        >
          <p className="cn-body">
            {busy
              ? t("Reading installed titles and drive availability from ShadowMount.")
              : t(
                  "Library uses ShadowMount’s local games API on your PS5. Start a version with that API enabled, then refresh here.",
                )}
          </p>
          {!busy && (
            <p className="cn-body">
              {t("Games are not inferred from download history. An unavailable library does not mean your games are missing.")}
            </p>
          )}
          {error && <p className="cn-body cn-error">{error}</p>}
          <div className="cn-actions">{refresh}</div>
        </CinemaStage>
        {dialogs}
      </section>
    );
  }
  // ---- Storage: one drive on the stage ----
  if (section === "storage") {
    const list = model.storage?.drives || [];
    const drive = list.find((d) => d.id === drivePick) || list[0];
    const onDrive = gamesOnDrive(model, drive);
    const measured = onDrive.filter((g) => g.sizeBytes !== null).length;
    return (
      <section className="cn-page cn-library">
        <CinemaArt />
        {collections}
        <LibraryFeedback model={model} />
        {drive ? (
          <CinemaStage
            kicker={`${drive.path}${drive.readOnly ? ` · ${t("Read only")}` : ""} · ${t("{used} used of {total}", {
              used: bytes(drive.usedBytes),
              total: bytes(drive.totalBytes),
            })}`}
            title={placeName(drive.label)}
          >
            <p className="cn-body">
              {onDrive
                .slice(0, 4)
                .map((g) => `${g.title}${g.sizeBytes !== null ? ` ${bytes(g.sizeBytes)}` : ""}`)
                .join(" · ")}
              {onDrive.length > 0 && " · "}
              {tn(onDrive.length, "{count} game", "{count} games")} ·{" "}
              {tn(measured, "{count} size measured", "{count} sizes measured")}
            </p>
            <p className="cn-body">
              {t(
                "Sizes are measured on request. Shared source files can appear under multiple titles; drive usage includes other files.",
              )}
            </p>
            {(model.storageError || model.storage?.error) && (
              <p className="cn-body cn-error">{t(model.storageError || model.storage?.error || "")}</p>
            )}
            <StorageProgress model={model} />
            <div className="cn-actions">
              <button
                className="cn-button primary"
                disabled={model.working || !model.can("list_games")}
                onClick={() => void model.run("measure").catch(() => undefined)}
              >
                {model.snapshot?.action?.action === "measure" && model.actionBusy ? t("Measuring…") : t("Measure game sizes")}
              </button>
              <button className="cn-button" disabled={!!model.storage?.busy} onClick={() => void model.readStorage(true)}>
                <Glyph name="refresh" size={18} />
                {model.storage?.busy ? t("Refreshing…") : t("Refresh drives")}
              </button>
              {model.can("rescan") && (
                <button className="cn-button" disabled={model.working} onClick={openScan}>
                  <Icon name="scan" />
                  {t("Scan for games")}
                </button>
              )}
            </div>
          </CinemaStage>
        ) : (
          <CinemaStage kicker={t("Storage")} title={model.storage?.busy ? t("Reading drives…") : t("No drives reported")}>
            <p className="cn-body">{t("See available space and manage where your games live.")}</p>
            <div className="cn-actions">
              <button className="cn-button primary" onClick={() => void model.readStorage(true)}>
                {t("Refresh drives")}
              </button>
            </div>
          </CinemaStage>
        )}
        {list.length > 0 && (
          <CinemaStrip
            label={t("Storage")}
            frames={list.map((d) => ({ key: d.id, label: placeName(d.label), text: `${placeName(d.label)} · ${t("{size} free", { size: bytes(d.freeBytes) })}` }))}
            current={drive?.id}
            choose={setDrivePick}
            stepCollection={step}
            canStep={(delta) => index + delta >= 0 && index + delta < names.length}
          />
        )}
        {dialogs}
      </section>
    );
  }
  // ---- Games ----
  const current = rows.find((g) => g.titleId === selected) || rows[0];
  const filters = (
    <div className="cn-filterline">
      <label className="search-field">
        <Icon name="search" />
        <input aria-label={t("Search library")} value={query} onChange={(e) => setQuery(e.target.value)} placeholder={t("Search your library")} />
      </label>
      <Select
        label={t("Location")}
        value={location}
        onChange={setLocation}
        options={[
          { value: "", label: t("All locations") },
          ...[...new Set(games.map((g) => g.location))].sort().map((value) => ({ value, label: placeName(value) })),
        ]}
      />
    </div>
  );
  const frames: CinemaFrame[] = rows.map((g) => ({
    key: g.titleId,
    label: g.title,
    art: g.cover,
    fallback: g.coverFallback || undefined,
    text: g.title,
    badge: statusOf(g),
  }));
  return (
    <section className="cn-page cn-library">
      <CinemaArt src={current?.cover} fallback={current?.coverFallback || undefined} />
      <div className="cn-pagehead">
        {collections}
        {filters}
      </div>
      {lookupToast}
      {error && <p className="cn-body cn-error">{error}</p>}
      {current ? (
        <>
          {details && (
            <aside className="cn-details" aria-label={t("Library game details")}>
              <p className="cn-card-kicker">{t("Library game details")}</p>
              <dl>
                <div>
                  <dt>{t("Installation")}</dt>
                  <dd>{current.installed ? t("Registered on the console") : t("Not installed on the console")}</dd>
                </div>
                <div>
                  <dt>{t("Mount status")}</dt>
                  <dd>{current.mounted ? t("Mounted") : t("Not mounted")}</dd>
                </div>
                <div>
                  <dt>{t("Source")}</dt>
                  <dd>{current.onDrive ? t("Available on drive") : t("Unavailable; the drive or source may be missing")}</dd>
                </div>
                <div>
                  <dt>{t("Format")}</dt>
                  <dd>{current.format}</dd>
                </div>
                <div>
                  <dt>{t("Source path")}</dt>
                  <dd className="library-path">{current.path || t("Not reported")}</dd>
                </div>
                {current.runtimePath && current.runtimePath !== current.path && (
                  <div>
                    <dt>{t("Game path")}</dt>
                    <dd className="library-path">{current.runtimePath}</dd>
                  </div>
                )}
                <div>
                  <dt>{t("Source size")}</dt>
                  <dd>
                    {current.sizeBytes !== null
                      ? bytes(current.sizeBytes)
                      : current.sizeStatus === "unavailable"
                        ? t("Unavailable")
                        : t("Use Measure game sizes in Storage")}
                  </dd>
                </div>
                <div>
                  <dt>{t("Size reported by console")}</dt>
                  <dd>{current.installedSizeBytes === null ? t("Not reported") : bytes(current.installedSizeBytes)}</dd>
                </div>
              </dl>
            </aside>
          )}
          <CinemaStage
            kicker={[
              statusOf(current),
              current.onDrive ? placeName(current.location) : t("Drive disconnected"),
              stale && model.snapshot?.updatedAt
                ? t("Saved snapshot · Last refreshed {time}", {
                    time: formatDate(new Date(model.snapshot.updatedAt * 1000), {
                      month: "short",
                      day: "numeric",
                      hour: "2-digit",
                      minute: "2-digit",
                    }),
                  })
                : "",
            ]
              .filter(Boolean)
              .join(" · ")}
            title={current.title}
          >
            <p className="cn-body">
              {t(
                "Installed means registered on the console. Mounted means its source is currently mounted. On drive means the source is available. These statuses do not confirm that a game will launch.",
              )}
            </p>
            <div className="cn-game-actions">
              <GameActions
                game={current}
                model={model}
                driveCards
                extra={
                  <button className="cn-button" aria-expanded={details} onClick={() => setDetails((v) => !v)}>
                    <Glyph name="info" size={18} />
                    {details ? t("Hide details") : t("Details")}
                  </button>
                }
              />
            </div>
          </CinemaStage>
          <CinemaStrip
            label={t(names[index])}
            frames={frames}
            current={current.titleId}
            choose={(id) => setSelected(id)}
            stepCollection={step}
            canStep={(delta) => index + delta >= 0 && index + delta < names.length}
          />
        </>
      ) : (
        <CinemaStage kicker={t(names[index])} title={games.length ? t("No matching games") : t("No games reported yet")}>
          <p className="cn-body">
            {games.length
              ? t("Try a different title, status or location.")
              : t("ShadowMount has not reported any installed or on-drive games. Refresh after its inventory updates.")}
          </p>
          <div className="cn-actions">
            {games.length > 0 && (
              <button className="cn-button primary" onClick={reset}>
                {t("Reset library filters")}
              </button>
            )}
            {refresh}
          </div>
        </CinemaStage>
      )}
      <div className="cn-capacity" aria-label={t("Available storage")}>
        {drives.map((drive) => (
          <span key={drive.id}>
            {placeName(drive.label)} <span>{t("{size} free", { size: bytes(drive.freeBytes) })}</span>
          </span>
        ))}
      </div>
      {dialogs}
    </section>
  );
}
