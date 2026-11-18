import { useEffect, useState } from "react";
import { BackHandler } from "react-native";

/**
 * Items being chosen on a photo screen. Choosing starts with a long press;
 * the system's back button ends it before it leaves the screen.
 */
export function useSelection() {
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const active = selected.size > 0;

  useEffect(() => {
    if (!active) {
      return;
    }

    const handler = BackHandler.addEventListener("hardwareBackPress", () => {
      setSelected(new Set());
      return true;
    });
    return () => handler.remove();
  }, [active]);

  return {
    /** The chosen ids, or undefined while nothing is being chosen. */
    selected: active ? selected : undefined,
    count: selected.size,
    toggle: (id: string) =>
      setSelected((before) => {
        const next = new Set(before);
        if (!next.delete(id)) {
          next.add(id);
        }

        return next;
      }),
    clear: () => setSelected(new Set()),
  };
}
