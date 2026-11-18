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
