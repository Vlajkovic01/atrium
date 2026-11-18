import type { components } from "@atrium/shared";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { Icon } from "../components/Icon.tsx";
import { currentLocale } from "../i18n.ts";
import { formatDuration, formatPhotoDay, groupByDay, justify } from "@atrium/shared";

export type PhotoItem = components["schemas"]["PhotoItemView"];

const ROW_HEIGHT = 180;
const GAP = 4;

/** "Thursday, 2 May 2024" for a day of local clock time (D-034). */
export function formatDay(day: string): string {
  return formatPhotoDay(day, currentLocale());
}

export { formatDuration } from "@atrium/shared";

/**
 * The photo grid (UX.md Photos): days with justified rows of thumbnails.
 * A click opens a photo; in selection mode, or with Ctrl, Cmd or Shift, it
 * selects. Thumbnails load as they scroll into view.
 */
export function PhotoGrid(props: {
  readonly items: readonly PhotoItem[];
  readonly selected: ReadonlySet<string>;
  readonly onSelect: (ids: ReadonlySet<string>) => void;
  readonly onOpen: (index: number) => void;
}) {
  const { t } = useTranslation();
  const container = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);
  const [anchor, setAnchor] = useState<number | null>(null);

  useEffect(() => {
    const element = container.current;
    if (!element) {
      return;
    }

    const observer = new ResizeObserver(([entry]) => setWidth(Math.floor(entry?.contentRect.width ?? 0)));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const indexOf = useMemo(() => new Map(props.items.map((item, index) => [item.id, index])), [props.items]);
  const days = useMemo(() => groupByDay(props.items), [props.items]);
  const selecting = props.selected.size > 0;

  const click = (item: PhotoItem, event: React.MouseEvent): void => {
    const index = indexOf.get(item.id) ?? 0;
    if (event.shiftKey && anchor !== null) {
      const [from, to] = anchor < index ? [anchor, index] : [index, anchor];
      const next = new Set(props.selected);
      props.items.slice(from, to + 1).forEach((i) => next.add(i.id));
      props.onSelect(next);
      return;
    }

    if (selecting || event.ctrlKey || event.metaKey) {
      toggle(item.id, index);
      return;
    }

    props.onOpen(index);
  };

  const toggle = (id: string, index: number): void => {
    const next = new Set(props.selected);
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }

    setAnchor(index);
    props.onSelect(next);
  };

  return (
    <div className="photo-grid" ref={container}>
      {width > 0
        ? days.map((day) => (
            <section key={day.day} className="photo-day" aria-label={formatDay(day.day)}>
              <h2 className="photo-day-title">{formatDay(day.day)}</h2>
              {justify(day.items, width, ROW_HEIGHT, GAP).map((row, r) => (
                <div key={r} className="photo-row">
                  {row.map(({ item, width: w, height: h }) => {
                    const isSelected = props.selected.has(item.id);
                    return (
                      <div
                        key={item.id}
                        className={isSelected ? "photo-tile is-selected" : "photo-tile"}
                        style={{ width: `${w}px`, height: `${h}px` }}
                      >
                        <button
                          type="button"
                          className="photo-open"
                          aria-label={item.name}
                          aria-pressed={selecting ? isSelected : undefined}
                          onClick={(event) => click(item, event)}
                        >
                          <img src={item.thumbnailUrl} alt="" loading="lazy" decoding="async" draggable={false} />
                          {item.kind === "video" ? (
                            <span className="photo-badge">
                              <Icon name="play" size={12} />
                              {item.durationMs != null ? formatDuration(Number(item.durationMs)) : null}
                            </span>
                          ) : null}
                        </button>
                        <button
                          type="button"
                          className="photo-check"
                          aria-label={
                            isSelected
                              ? t("photos.deselect", { name: item.name })
                              : t("photos.select", { name: item.name })
                          }
                          aria-pressed={isSelected}
                          onClick={() => toggle(item.id, indexOf.get(item.id) ?? 0)}
                        >
                          <Icon name="check" size={14} />
                        </button>
                      </div>
                    );
                  })}
                </div>
              ))}
            </section>
          ))
        : null}
    </div>
  );
}
