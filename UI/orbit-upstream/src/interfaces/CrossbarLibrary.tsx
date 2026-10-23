// Crossbar's Library: views as folders, the games as a list with the focused
// game's buttons on its row and its details beside it, then Storage and the
// scan. Actions, confirmations and the scan are Library's own.
import { useState, type ReactNode } from "react";
import { bytes, type LibraryGame, type Storage } from "../types";
import type { LibraryModel } from "../useLibrary";
import { placeName, t, tn } from "../i18n";
import { Artwork } from "../Artwork";
import { GameActions, gamesOnDrive, LibraryFeedback, StorageProgress } from "../LibraryActions";
import { CrossbarList, type XbRow } from "./CrossbarList";
import { Glyph, type GlyphName } from "./Glyph";

const statusOf = (g: LibraryGame) =>
  !g.onDrive ? t("Unavailable") : g.mounted ? t("Mounted") : g.installed ? t("Installed") : t("On drive");
const viewGlyph: Record<string, GlyphName> = { Installed: "check", "On drive": "drive", Unavailable: "warn" };

export function CrossbarLibrary({
  model,
  games,
  rows,
  views,
  view,
  setView,
  section,
  setSection,
  ready,
  busy,
  error,
  openScan,
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
  ready: boolean;
  busy: boolean;
  error: string;
  openScan: () => void;
  dialogs: ReactNode;
  drives: Storage[];
}) {
  const [current, setCurrent] = useState<string | undefined>();
  const folderRow = (key: string, label: string, sub: string, glyph: GlyphName, open: () => void): XbRow => ({
    key,
    activate: open,
    render: () => (
      <>
        <span className="xb-row-icon">
          <Glyph name={glyph} size={22} />
        </span>
        <span className="xb-row-text">
          <span className="xb-row-title">{label}</span>
          <span className="xb-row-sub">{sub}</span>
        </span>
        <Glyph name="next" size={18} />
      </>
    ),
  });
  const leave = () => {
    if (section === "storage") {
      setSection("games");
      setCurrent("folder-storage");
      return true;
    }
    if (view !== views[0]) {
      setCurrent(`folder-${view}`);
      setView(views[0]);
      return true;
    }
    return false;
  };
  if (!ready) {
    return (
      <section className="xb-page xb-library">
        <div className="xb-split">
          <div className="xb-column">
            <div className="xb-empty">
              <h2>{busy ? t("Finding your games") : t("Connect your library")}</h2>
              <p>
                {busy
                  ? t("Reading installed titles and drive availability from ShadowMount.")
                  : t("Library uses ShadowMount’s local games API on your PS5. Start a version with that API enabled, then refresh here.")}
              </p>
              {error && <p className="warn">{error}</p>}
              <button className="xb-pill primary" disabled={busy || Date.now() < model.refreshAt} onClick={() => void model.read(true)}>
                {busy ? t("Refreshing…") : t("Refresh library")}
              </button>
            </div>
          </div>
        </div>
        {dialogs}
      </section>
    );
  }
  if (section === "storage") {
    const list = model.storage?.drives || [];
    const chosen = list.find((d) => `drive-${d.id}` === current) || list[0];
    const onDrive = gamesOnDrive(model, chosen);
    const driveRows: XbRow[] = [
      ...list.map(
        (d): XbRow => ({
          key: `drive-${d.id}`,
          render: (focused) => (
            <>
              <span className="xb-row-icon">
                <Glyph name="drive" size={22} />
              </span>
              <span className="xb-row-text">
                <span className="xb-row-title">
                  {placeName(d.label)}
                  {d.readOnly && <span className="xb-tag">{t("Read only")}</span>}
                </span>
                <span className="xb-row-sub">{t("{free} free of {total}", { free: bytes(d.freeBytes), total: bytes(d.totalBytes) })}</span>
                <span className="xb-progress" aria-hidden="true">
                  <i style={{ width: `${d.totalBytes ? Math.min(100, (100 * d.usedBytes) / d.totalBytes) : 0}%` }} />
                </span>
                {focused && (
                  <span className="xb-row-fine">
                    {d.path} · {t("{used} used of {total}", { used: bytes(d.usedBytes), total: bytes(d.totalBytes) })}
                  </span>
                )}
              </span>
            </>
          ),
          actions: (
            <button
              className="primary"
              disabled={model.working || !model.can("list_games")}
              onClick={() => void model.run("measure").catch(() => undefined)}
            >
              <Glyph name="drive" size={15} stroke={2} />
              {model.snapshot?.action?.action === "measure" && model.actionBusy ? t("Measuring…") : t("Measure game sizes")}
            </button>
          ),
        }),
      ),
      {
        key: "refresh-drives",
        activate: () => void model.readStorage(true),
        render: () => (
          <>
            <span className="xb-row-icon">
              <Glyph name="refresh" size={22} />
            </span>
            <span className="xb-row-text">
              <span className="xb-row-title">{model.storage?.busy ? t("Refreshing…") : t("Refresh drives")}</span>
            </span>
          </>
        ),
      },
    ];
    return (
      <section className="xb-page xb-library">
        <div className="xb-split">
          <div className="xb-column">
            <p className="xb-breadcrumb">
              {t("Library")} <span aria-hidden="true">›</span> {t("Storage")}
            </p>
            <CrossbarList rows={driveRows} current={current} setCurrent={setCurrent} back={leave} ariaLabel={t("Storage")} />
          </div>
          <aside className="xb-aside xb-card">
            <p className="xb-card-kicker">{t("Games on {drive}", { drive: placeName(chosen?.label || "") })}</p>
            <ul className="xb-card-list">
              {onDrive.map((g) => (
                <li key={g.titleId}>
                  <span className="xb-card-art">
                    {g.cover && <Artwork src={g.cover} fallbackSrc={g.coverFallback || undefined} alt="" decoding="async" referrerPolicy="no-referrer" />}
                  </span>
                  <span>{g.title}</span>
                  <span>{statusOf(g)}</span>
                  <span>{g.sizeBytes !== null ? bytes(g.sizeBytes) : g.sizeStatus === "unavailable" ? t("Size unavailable") : t("Not measured")}</span>
                </li>
              ))}
            </ul>
            <p className="xb-card-fine">
              {tn(onDrive.length, "{count} game", "{count} games")} · {" "}
              {t("Sizes are measured on request. Shared source files can appear under multiple titles; drive usage includes other files.")}
            </p>
            {(model.storageError || model.storage?.error) && <p className="xb-card-error">{t(model.storageError || model.storage?.error || "")}</p>}
            <StorageProgress model={model} />
            <LibraryFeedback model={model} />
          </aside>
        </div>
        {dialogs}
      </section>
    );
  }
  const focusedGame = rows.find((g) => `game-${g.titleId}` === current) || rows[0];
  const gameRows: XbRow[] = rows.map((g) => ({
    key: `game-${g.titleId}`,
    disabled: !g.onDrive,
    render: () => (
      <>
        <span className="xb-row-art">
          {g.cover ? (
            <Artwork src={g.cover} fallbackSrc={g.coverFallback || undefined} alt="" decoding="async" referrerPolicy="no-referrer" />
          ) : (
            <Glyph name="box" size={22} />
          )}
        </span>
        <span className="xb-row-text">
          <span className="xb-row-title">{g.title}</span>
          <span className={`xb-row-sub${!g.onDrive ? " warn" : ""}`}>
            {statusOf(g)} · {g.onDrive ? placeName(g.location) : t("Drive disconnected")}
          </span>
        </span>
      </>
    ),
    actions: <GameActions game={g} model={model} driveCards />,
  }));
  const allRows: XbRow[] = [
    ...(view === views[0]
      ? views.slice(1).map((v) =>
          folderRow(`folder-${v}`, t(v), tn(games.filter((g) => (v === "Installed" ? g.installed : v === "On drive" ? g.onDrive : !g.onDrive)).length, "{count} game", "{count} games"), viewGlyph[v] || "folder", () => {
            setView(v);
            setCurrent(undefined);
          }),
        )
      : []),
    ...gameRows,
    ...(view === views[0]
      ? [
          folderRow("folder-storage", t("Storage"), tn(drives.length, "{count} drive", "{count} drives"), "drive", () => {
            setSection("storage");
            setCurrent(undefined);
          }),
          ...(model.can("rescan")
            ? [folderRow("scan", t("Scan for games"), t("Search your drives with ShadowMount"), "scan", openScan)]
            : []),
        ]
      : []),
  ];
  return (
    <section className="xb-page xb-library">
      <div className="xb-split">
        <div className="xb-column">
          {view !== views[0] && (
            <p className="xb-breadcrumb">
              {t("Library")} <span aria-hidden="true">›</span> {t(view)}
            </p>
          )}
          <CrossbarList rows={allRows} current={current || (focusedGame ? `game-${focusedGame.titleId}` : undefined)} setCurrent={setCurrent} back={leave} ariaLabel={t("Library")} />
        </div>
        {focusedGame && (
          <aside className="xb-aside xb-card" aria-live="polite">
            <p className="xb-card-kicker">
              {focusedGame.platform === "unknown" ? t("Platform unknown") : focusedGame.platform.toUpperCase()} · {focusedGame.titleId}
              {model.stale && <span className="xb-tag">{t("Saved snapshot")}</span>}
            </p>
            <h2>{focusedGame.title}</h2>
            <dl className="xb-facts">
              <div>
                <dt>{t("Installation")}</dt>
                <dd>{focusedGame.installed ? t("Registered on the console") : t("Not installed on the console")}</dd>
              </div>
              <div>
                <dt>{t("Mount status")}</dt>
                <dd>{focusedGame.mounted ? t("Mounted") : t("Not mounted")}</dd>
              </div>
              <div>
                <dt>{t("Source")}</dt>
                <dd>{focusedGame.onDrive ? t("Available on drive") : t("Unavailable; the drive or source may be missing")}</dd>
              </div>
              <div>
                <dt>{t("Format")}</dt>
                <dd>{focusedGame.format}</dd>
              </div>
              <div>
                <dt>{t("Source path")}</dt>
                <dd className="library-path">{focusedGame.path || t("Not reported")}</dd>
              </div>
              {focusedGame.runtimePath && focusedGame.runtimePath !== focusedGame.path && (
                <div>
                  <dt>{t("Game path")}</dt>
                  <dd className="library-path">{focusedGame.runtimePath}</dd>
                </div>
              )}
              <div>
                <dt>{t("Source size")}</dt>
                <dd>
                  {focusedGame.sizeBytes !== null
                    ? bytes(focusedGame.sizeBytes)
                    : focusedGame.sizeStatus === "unavailable"
                      ? t("Unavailable")
                      : t("Use Measure game sizes in Storage")}
                </dd>
              </div>
              <div>
                <dt>{t("Size reported by console")}</dt>
                <dd>{focusedGame.installedSizeBytes === null ? t("Not reported") : bytes(focusedGame.installedSizeBytes)}</dd>
              </div>
            </dl>
            <p className="xb-card-fine">
              {t(
                "Installed means registered on the console. Mounted means its source is currently mounted. On drive means the source is available. These statuses do not confirm that a game will launch.",
              )}
            </p>
            {error && <p className="xb-card-error">{error}</p>}
          </aside>
        )}
      </div>
      {dialogs}
    </section>
  );
}
