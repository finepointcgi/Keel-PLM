import type { PageBanner } from "../lib/markings";

export function MarkingBanner({ banner, position }: { banner: PageBanner; position: "top" | "bottom" }) {
  return (
    <div className={`marking-banner tone-${banner.tone}`} role="note" aria-label={`Marking banner (${position})`} data-position={position}>
      {banner.text}
    </div>
  );
}
