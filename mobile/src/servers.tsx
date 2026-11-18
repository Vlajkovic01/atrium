import type { KnownServer, ServerRegistry } from "@atrium/shared";
import { createContext, useContext, useEffect, useState, type ReactNode } from "react";

import { loadRegistry } from "./registry.ts";

interface ServersValue {
  readonly registry: ServerRegistry;
  readonly servers: readonly KnownServer[];
  /** The server in front, or null when the app is connected to none. */
  readonly active: KnownServer | null;
}

const ServersContext = createContext<ServersValue | null>(null);

export function useServers(): ServersValue {
  const value = useContext(ServersContext);
  if (!value) {
    throw new Error("useServers must be used inside ServersProvider");
  }

  return value;
}

/**
 * The servers this app is signed in to (several, each with its own
 * session). Read from the device once at start; screens are drawn again
 * whenever the list or the server in front changes.
 */
export function ServersProvider({ children }: { readonly children: ReactNode }) {
  const [registry, setRegistry] = useState<ServerRegistry | null>(null);
  const [, setRevision] = useState(0);

  useEffect(() => {
    let stop: (() => void) | undefined;
    let cancelled = false;
    void loadRegistry().then((loaded) => {
      if (!cancelled) {
        stop = loaded.onChange(() => setRevision((r) => r + 1));
        setRegistry(loaded);
      }
    });
    return () => {
      cancelled = true;
      stop?.();
    };
  }, []);

  if (!registry) {
    return null;
  }

  return (
    <ServersContext.Provider value={{ registry, servers: registry.servers, active: registry.active }}>
      {children}
    </ServersContext.Provider>
  );
}
