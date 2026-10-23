import { useEffect, useRef, type PointerEvent, type MouseEvent, type ReactNode } from 'react';

// One pointer gesture owns either a click or a hold. Keyboard/controller activation remains a click.
export function CoverLaunch({ choose, play, children, ...props }: {
  choose: () => void; play: () => void; children: ReactNode;
  [key: string]: unknown;
}) {
  const gesture = useRef<{ id: number; x: number; y: number; target: HTMLButtonElement; timer: ReturnType<typeof setTimeout> } | null>(null);
  const suppressClick = useRef(false);
  function cancel(suppress = true) {
    const current = gesture.current;
    gesture.current = null;
    if (current) {
      clearTimeout(current.timer);
      if (current.target.hasPointerCapture(current.id)) current.target.releasePointerCapture(current.id);
    }
    if (suppress) suppressClick.current = true;
  }
  useEffect(() => {
    const blur = () => cancel();
    window.addEventListener('blur', blur); document.addEventListener('visibilitychange', blur);
    return () => { cancel(); window.removeEventListener('blur', blur); document.removeEventListener('visibilitychange', blur); };
  }, []);
  function down(event: PointerEvent<HTMLButtonElement>) {
    if (event.button !== 0 || !event.isPrimary || props['aria-disabled'] === true) return;
    cancel(false); suppressClick.current = false;
    const target = event.currentTarget;
    target.setPointerCapture(event.pointerId);
    gesture.current = { id: event.pointerId, x: event.clientX, y: event.clientY, target,
      timer: setTimeout(() => { cancel(); choose(); }, 650) };
  }
  return <button {...props} onPointerDown={down} onPointerMove={event => {
    const current = gesture.current;
    if (current) {
      const rect = current.target.getBoundingClientRect();
      if (Math.hypot(event.clientX - current.x, event.clientY - current.y) > 10 ||
          event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) cancel();
    }
  }} onPointerUp={() => cancel(false)} onPointerCancel={() => cancel()} onLostPointerCapture={() => {
    if (gesture.current) cancel();
  }} onClick={(event: MouseEvent<HTMLButtonElement>) => {
    if (event.detail > 0 && suppressClick.current) { event.preventDefault(); return; }
    play();
  }}>{children}</button>;
}
