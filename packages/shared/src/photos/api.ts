import { call } from "../api/call.ts";
import type { ApiClient } from "../api/client.ts";
import type { components } from "../api/schema.d.ts";

export type PhotoItem = components["schemas"]["PhotoItemView"];
export type PhotoPage = components["schemas"]["PhotoPageView"];
export type TimelineBucket = components["schemas"]["TimelineBucketView"];
export type MediaInfo = components["schemas"]["MediaInfoView"];

/**
 * The calls of the photo screens, the same for every client. Each throws
 * an `ApiError` with the server's code, or "network".
 */
export function photosApi(api: ApiClient) {
  return {
    /**
     * Photos and videos, newest first, a page at a time. `to` starts the
     * page at a moment (exclusive), for jumping to a month.
     */
    timeline: (options: {
      readonly limit?: number;
      readonly to?: string;
      readonly cursor?: string | null;
    }): Promise<PhotoPage> =>
      call(
        api.GET("/api/v1/timeline", {
          params: {
            query: {
              ...(options.limit ? { limit: options.limit } : {}),
              ...(options.to ? { to: options.to } : {}),
              ...(options.cursor ? { cursor: options.cursor } : {}),
            },
          },
        }),
      ),

    /** How many photos and videos each month holds, newest month first. */
    buckets: (): Promise<TimelineBucket[]> => call(api.GET("/api/v1/timeline/buckets")),

    /** What is known of one photo or video: size, length, camera, when it was taken. */
    mediaInfo: (id: string): Promise<MediaInfo> =>
      call(api.GET("/api/v1/nodes/{id}/media-info", { params: { path: { id } } })),
  };
}

export type PhotosApi = ReturnType<typeof photosApi>;

export type AlbumView = components["schemas"]["AlbumView"];

/**
 * The person's albums. An album only points at photos and videos: adding,
 * removing or deleting an album never touches a file.
 */
export function albumsApi(api: ApiClient) {
  const update = (album: Pick<AlbumView, "id" | "version">, name: string | null, coverNodeId: string | null) =>
    call(
      api.PATCH("/api/v1/albums/{id}", {
        params: { path: { id: album.id } },
        headers: { "If-Match": `"${album.version}"` },
        body: { name, coverNodeId },
      }),
    );

  return {
    list: (): Promise<AlbumView[]> => call(api.GET("/api/v1/albums")),

    get: (id: string): Promise<AlbumView> => call(api.GET("/api/v1/albums/{id}", { params: { path: { id } } })),

    /** An album's photos and videos, newest first, a page at a time. */
    items: (id: string, options: { readonly limit?: number; readonly cursor?: string | null }): Promise<PhotoPage> =>
      call(
        api.GET("/api/v1/albums/{id}/items", {
          params: {
            path: { id },
            query: {
              ...(options.limit ? { limit: options.limit } : {}),
              ...(options.cursor ? { cursor: options.cursor } : {}),
            },
          },
        }),
      ),

    create: (name: string, nodeIds: readonly string[] = []): Promise<AlbumView> =>
      call(api.POST("/api/v1/albums", { body: { name, nodeIds: [...nodeIds] } })),

    /** A new name. The version the person was looking at goes along, so a change made elsewhere is not overwritten. */
    rename: (album: Pick<AlbumView, "id" | "version">, name: string): Promise<AlbumView> => update(album, name, null),

    /** One of the album's photos as its cover, under the same version check as `rename`. */
    setCover: (album: Pick<AlbumView, "id" | "version">, nodeId: string): Promise<AlbumView> =>
      update(album, null, nodeId),

    /** Deletes the album; its photos and videos stay where they are. */
    delete: async (id: string): Promise<void> => {
      await call(api.DELETE("/api/v1/albums/{id}", { params: { path: { id } } }));
    },

    /** Returns how many were not in the album yet. */
    add: async (id: string, nodeIds: readonly string[]): Promise<number> =>
      Number(
        (
          await call(
            api.POST("/api/v1/albums/{id}/items", { params: { path: { id } }, body: { nodeIds: [...nodeIds] } }),
          )
        ).changed,
      ),

    remove: async (id: string, nodeIds: readonly string[]): Promise<number> =>
      Number(
        (
          await call(
            api.POST("/api/v1/albums/{id}/items/remove", { params: { path: { id } }, body: { nodeIds: [...nodeIds] } }),
          )
        ).changed,
      ),
  };
}

export type AlbumsApi = ReturnType<typeof albumsApi>;

export type SearchResult = components["schemas"]["SearchResultView"];
export type SearchPage = components["schemas"]["SearchPageView"];
export type SearchType = components["schemas"]["SearchType"];

/** The kinds a search can be narrowed to, in the order they are offered. */
export const searchTypes: readonly SearchType[] = ["photo", "video", "document", "folder"];

/** A search result that is a photo or video, in the shape the photo grid uses; null for anything else. */
export function searchResultAsPhoto(result: SearchResult): PhotoItem | null {
  if (!result.kind || !result.thumbnailUrl || !result.capturedAt) {
    return null;
  }

  return {
    id: result.node.id,
    libraryId: result.node.libraryId,
    parentId: result.node.parentId,
    name: result.node.name,
    kind: result.kind,
    mediaType: result.node.mediaType,
    size: result.node.size,
    capturedAt: result.capturedAt,
    width: result.width,
    height: result.height,
    durationMs: null,
    thumbnailUrl: result.thumbnailUrl,
  };
}

/**
 * Searches names, kinds and dates everywhere the person can read. The
 * words and the date range come from `parseSearch`.
 */
export function searchApi(api: ApiClient) {
  return {
    search: (options: {
      readonly words?: string;
      readonly type?: SearchType | null;
      readonly range?: { readonly from: string; readonly to: string } | null;
      readonly limit?: number;
      readonly cursor?: string | null;
    }): Promise<SearchPage> =>
      call(
        api.GET("/api/v1/search", {
          params: {
            query: {
              ...(options.limit ? { limit: options.limit } : {}),
              ...(options.words ? { q: options.words } : {}),
              ...(options.type ? { type: options.type } : {}),
              ...(options.range ? { from: options.range.from, to: options.range.to } : {}),
              ...(options.cursor ? { cursor: options.cursor } : {}),
            },
          },
        }),
      ),
  };
}

export type SearchApi = ReturnType<typeof searchApi>;
