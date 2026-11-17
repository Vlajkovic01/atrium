import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { AppState, StyleSheet, View } from "react-native";

import { LogoMark } from "../components/Logo.tsx";
import { Body, Button, Title } from "../components/ui.tsx";
import { canLock, unlock } from "../platform/biometric";
import { settings } from "../platform/storage";
import { space, useTheme } from "../theme.ts";

/** How long the app may stay in the background before it locks again, in minutes. 0 is at once. */
export const lockAfterChoices = [0, 1, 5, 15] as const;
export type LockAfter = (typeof lockAfterChoices)[number];

const ENABLED = "atrium.lock.enabled";
const AFTER = "atrium.lock.after";

interface LockValue {
  readonly enabled: boolean;
  readonly after: LockAfter;
  /** Whether this phone can lock the app at all: no fingerprint, face or screen lock means no. */
  readonly available: boolean;
  /** Turning the lock on asks to unlock once, so a lock nobody can open is never set. */
  readonly setEnabled: (on: boolean) => Promise<void>;
  readonly setAfter: (minutes: LockAfter) => void;
}

const LockContext = createContext<LockValue | null>(null);

export function useLock(): LockValue {
  const value = useContext(LockContext);
  if (!value) {
    throw new Error("useLock must be used inside LockProvider");
  }

  return value;
}

/**
 * The optional lock (docs/UX.md, Mobile, Security): off by default. When
 * on, the app opens locked and locks again after the chosen time in the
 * background; while locked, the lock screen covers all of it. Screenshots
 * and the list of open apps stay as the phone has them (owner, D-057). The
 * phone's own prompt does the checking.
 */
export function LockProvider({ children }: { readonly children: ReactNode }) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const [ready, setReady] = useState(false);
  const [enabled, setEnabledState] = useState(false);
  const [after, setAfterState] = useState<LockAfter>(1);
  const [available, setAvailable] = useState(false);
  const [locked, setLocked] = useState(false);
  const [asking, setAsking] = useState(false);
  const leftAt = useRef<number | null>(null);

  const ask = async (): Promise<boolean> => {
    setAsking(true);
    try {
      return await unlock(t("mobile.lock.prompt"), t("common.cancel"));
    } catch {
      return false;
    } finally {
      setAsking(false);
    }
  };

  // What was chosen on this phone; the app opens locked when the lock is on.
  useEffect(() => {
    void (async () => {
      const on = (await settings.get(ENABLED).catch(() => null)) === "1";
      const stored = Number(await settings.get(AFTER).catch(() => null));
      setAfterState((lockAfterChoices as readonly number[]).includes(stored) ? (stored as LockAfter) : 1);
      setAvailable(await canLock().catch(() => false));
      setEnabledState(on);
      setLocked(on);
      setReady(true);
    })();
  }, []);

  // Locking again after the chosen time away.
  useEffect(() => {
    const listener = AppState.addEventListener("change", (state) => {
      if (state === "background") {
        leftAt.current = Date.now();
      } else if (state === "active" && enabled && leftAt.current !== null) {
        if (Date.now() - leftAt.current >= after * 60_000) {
          setLocked(true);
        }

        leftAt.current = null;
      }
    });
    return () => listener.remove();
  }, [after, enabled]);

  // Asked at once when locked, without a touch first.
  useEffect(() => {
    if (locked && !asking) {
      void ask().then((ok) => ok && setLocked(false));
    }
    // The prompt is asked once each time the app becomes locked.
  }, [locked]);

  const value: LockValue = {
    enabled,
    after,
    available,
    setEnabled: async (on) => {
      if (on && !(await ask())) {
        return;
      }

      setEnabledState(on);
      await settings.set(ENABLED, on ? "1" : "0").catch(() => undefined);
    },
    setAfter: (minutes) => {
      setAfterState(minutes);
      void settings.set(AFTER, String(minutes)).catch(() => undefined);
    },
  };

  if (!ready) {
    return <View style={[styles.cover, { backgroundColor: colors.paper }]} />;
  }

  return (
    <LockContext.Provider value={value}>
      {/* The app stays where it was beneath, so unlocking returns to the same screen. */}
      {children}
      {locked ? (
        <View
          accessibilityViewIsModal
          style={[StyleSheet.absoluteFill, styles.center, { backgroundColor: colors.paper }]}
        >
          <LogoMark size={64} />
          <Title>{t("mobile.lock.locked")}</Title>
          <Body faint>{t("mobile.lock.lockedBody")}</Body>
          <Button variant="primary" busy={asking} onPress={() => void ask().then((ok) => ok && setLocked(false))}>
            {t("mobile.lock.unlock")}
          </Button>
        </View>
      ) : null}
    </LockContext.Provider>
  );
}

const styles = StyleSheet.create({
  cover: { flex: 1 },
  center: { alignItems: "center", justifyContent: "center", gap: space.lg, padding: space.xl },
});
