import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { PnGroup, PnTile } from "../orbit-upstream/src/interfaces/PanoramaParts";
import { t } from "../orbit-upstream/src/i18n";
import "./media-gallery.css";

type MediaItem = { type: "image" | "video"; title: string; thumbnailUrl: string; imageUrl: string; videoId: string; size: "small" | "wide" | "large" };
type MediaResult = { available: boolean; artworks: MediaItem[]; screenshots: MediaItem[]; trailers: MediaItem[]; message?: string | null };

export function MediaGallery({ gameTitle }: { gameTitle: string }) {
  const [result, setResult] = useState<MediaResult | null>(null);
  const [failed, setFailed] = useState(false);
  const [active, setActive] = useState<MediaItem | null>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const returnFocus = useRef<HTMLElement | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setResult(null);
    setFailed(false);
    void fetch("/api/v1/media", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title: gameTitle }),
      signal: controller.signal,
    }).then(async response => {
      if (!response.ok) throw new Error("Media lookup failed");
      setResult(await response.json() as MediaResult);
    }).catch(() => { if (!controller.signal.aborted) setFailed(true); });
    return () => controller.abort();
  }, [gameTitle]);

  useEffect(() => {
    if (!active) return;
    returnFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const root = document.getElementById("root");
    const wasInert = root?.inert ?? false;
    if (root) root.inert = true;
    requestAnimationFrame(() => closeRef.current?.focus({ preventScroll: true }));
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") { event.preventDefault(); setActive(null); }
      if (event.key === "Tab" && event.shiftKey && document.activeElement === closeRef.current) {
        event.preventDefault();
        const frame = document.querySelector<HTMLIFrameElement>(".media-viewer-video");
        if (frame) frame.focus({ preventScroll: true });
      }
    };
    document.addEventListener("keydown", onKeyDown, true);
    return () => {
      document.removeEventListener("keydown", onKeyDown, true);
      if (root) root.inert = wasInert;
      requestAnimationFrame(() => returnFocus.current?.focus({ preventScroll: true }));
    };
  }, [active]);

  const loading = result === null && !failed;
  const message = failed ? t("Game media is unavailable. Try again later.") : result?.message;
  const groups = [
    { category: "artwork" as const, title: "Artwork", items: result?.available ? result.artworks : [] },
    { category: "screenshots" as const, title: "Screenshots", items: result?.available ? result.screenshots : [] },
    { category: "trailers" as const, title: "Trailers", items: result?.available ? result.trailers : [] },
  ];
  const totalTiles = groups.reduce((total, item) => total + item.items.length, 0);
  return <>
    {loading && groups.map(group => <PnGroup key={group.category} title={t(group.title)} className={`media-gallery-group media-gallery-${group.category}-group`}>
      <div className="pn-tile wide media-gallery-skeleton" aria-hidden="true" />
    </PnGroup>)}
    {!loading && result?.available && groups.filter(group => group.items.length > 0).map(group => <PnGroup
      key={group.category}
      title={t(group.title)}
      className={`media-gallery-group media-gallery-${group.category}-group`}
    >
      {group.items.map((item, index) => <PnTile
        key={`${item.type}-${item.videoId || item.imageUrl}-${index}`}
        size={item.size}
        art={item.type === "video" ? `https://i.ytimg.com/vi/${item.videoId}/hqdefault.jpg` : item.thumbnailUrl}
        title={item.type === "image" ? undefined : item.title}
        label={`${item.type === "video" ? t("Play trailer") : t("View image")}: ${item.title}`}
        className={`media-gallery-tile media-gallery-${item.type}`}
        onClick={() => setActive(item)}
      />)}
    </PnGroup>)}
    {!loading && (message || result && (!result.available || totalTiles === 0)) && <p className="media-gallery-status" role="status">
      {message || t(result?.available ? "No game media was found." : "Game media is unavailable. Try again later.")}
    </p>}
    {active && createPortal(<div className="media-viewer-scrim">
      <section className="media-viewer" role="dialog" aria-modal="true" aria-label={active.type === "video" ? active.title : t("Image: {title}", { title: active.title })}>
        <button ref={closeRef} className="media-viewer-close" type="button" data-dialog-back aria-label={t("Close media viewer")} onClick={() => setActive(null)}>×</button>
        {active.type === "video" ? <iframe
          className="media-viewer-video"
          src={`https://www.youtube-nocookie.com/embed/${active.videoId}?autoplay=1&playsinline=0&rel=0`}
          title={active.title}
          allow="autoplay; encrypted-media; picture-in-picture; fullscreen"
          allowFullScreen
          referrerPolicy="strict-origin-when-cross-origin"
        /> : <img className="media-viewer-image" src={active.imageUrl} alt={active.title} referrerPolicy="no-referrer" />}
      </section>
    </div>, document.body)}
  </>;
}
