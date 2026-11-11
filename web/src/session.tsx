import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";

import {
  api,
  call,
  refreshSession,
  setAccessToken,
  setSignedOutHandler,
  type TokenResponse,
  type UserSummary,
} from "./api.ts";

interface SessionValue {
  /** Null while signed out. */
  readonly user: UserSummary | null;
  readonly sessionId: string | null;
  readonly signIn: (tokens: TokenResponse) => void;
  readonly signOut: () => Promise<void>;
}

const SessionContext = createContext<SessionValue | null>(null);

export function useSession(): SessionValue {
  const value = useContext(SessionContext);
  if (!value) {
    throw new Error("useSession must be used inside SessionProvider");
  }

  return value;
}

/**
 * Holds who is signed in. On start it tries the refresh cookie once: if
 * the browser still has a session, the user is signed in without typing
 * anything.
 */
export function SessionProvider({ children }: { readonly children: ReactNode }) {
  const [state, setState] = useState<{ user: UserSummary; sessionId: string } | null>(null);
  const [ready, setReady] = useState(false);

  const signIn = useCallback((tokens: TokenResponse) => {
    setAccessToken(tokens.accessToken);
    setState({ user: tokens.user, sessionId: tokens.sessionId });
  }, []);

  const signOut = useCallback(async () => {
    try {
      await call(api.POST("/api/v1/auth/logout"));
    } catch {
      // Even if the server could not be told, this browser forgets the session.
    }

    setAccessToken(null);
    setState(null);
  }, []);

  useEffect(() => {
    setSignedOutHandler(() => setState(null));
    let cancelled = false;
    void refreshSession().then((tokens) => {
      if (cancelled) {
        return;
      }

      if (tokens) {
        setState({ user: tokens.user, sessionId: tokens.sessionId });
      }

      setReady(true);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  const value = useMemo<SessionValue>(
    () => ({ user: state?.user ?? null, sessionId: state?.sessionId ?? null, signIn, signOut }),
    [state, signIn, signOut],
  );

  if (!ready) {
    return null;
  }

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}
