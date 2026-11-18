import {
  albumsApi,
  filesApi,
  photosApi,
  type AlbumsApi,
  type FilesApi,
  type KnownServer,
  type PhotosApi,
  type ServerSession,
} from "@atrium/shared";

import { useServers } from "./servers.tsx";

interface Calls {
  readonly files: FilesApi;
  readonly photos: PhotosApi;
  readonly albums: AlbumsApi;
}

const callsOf = new WeakMap<ServerSession, Calls>();

export interface ActiveServer {
  readonly server: KnownServer;
  readonly session: ServerSession;
  /** The file calls of the server in front, shared with the web app. */
  readonly files: FilesApi;
  /** The photo calls of the server in front. */
  readonly photos: PhotosApi;
  /** The album calls of the server in front. */
  readonly albums: AlbumsApi;
}

/**
 * The server in front and its calls, for screens that exist only while
 * signed in (everything under the bottom tabs). What a screen reads is
 * kept under the server's id, so two servers never show each other's
 * files.
 */
export function useActiveServer(): ActiveServer {
  const { registry, active } = useServers();
  if (!active) {
    throw new Error("This screen needs a server; the tabs open only when the app is connected to one.");
  }

  const session = registry.session(active.id);
  let calls = callsOf.get(session);
  if (!calls) {
    calls = { files: filesApi(session.api), photos: photosApi(session.api), albums: albumsApi(session.api) };
    callsOf.set(session, calls);
  }

  return { server: active, session, ...calls };
}
