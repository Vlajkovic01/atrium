import { Sha256, toHex, type DeviceAsset } from "@atrium/shared";
import * as Battery from "expo-battery";
import { File } from "expo-file-system";
import * as MediaLibrary from "expo-media-library/legacy";
import * as Network from "expo-network";

import { sizeOf } from "../uploads/source";

/** The phone's own photos and videos, for camera backup. */

/** What the person allowed: everything, only photos they picked (Android 14+), or nothing yet. */
export type MediaAccess = "all" | "limited" | "none" | "denied";

function accessOf(response: MediaLibrary.PermissionResponse): MediaAccess {
  if (response.granted) {
    return response.accessPrivileges === "limited" ? "limited" : "all";
  }

  return response.canAskAgain ? "none" : "denied";
}

export async function mediaAccess(): Promise<MediaAccess> {
  return accessOf(await MediaLibrary.getPermissionsAsync(false, ["photo", "video"]));
}

export async function askMediaAccess(): Promise<MediaAccess> {
  return accessOf(await MediaLibrary.requestPermissionsAsync(false, ["photo", "video"]));
}

/** The device albums photos and videos are in ("Camera", "Screenshots", …). */
export async function deviceAlbums(): Promise<readonly { readonly id: string; readonly title: string }[]> {
  return (await MediaLibrary.getAlbumsAsync()).map((a) => ({ id: a.id, title: a.title }));
}

/** An asset with where its content is on the phone. */
export interface PhoneAsset extends DeviceAsset {
  readonly uri: string;
}

/** Every photo and video the app may read, newest first, a page at a time. */
export async function listAssets(): Promise<PhoneAsset[]> {
  const albums = new Map((await deviceAlbums()).map((a) => [a.id, a.title]));
  const out: PhoneAsset[] = [];
  let after: string | undefined;
  for (;;) {
    const page = await MediaLibrary.getAssetsAsync({
      first: 500,
      ...(after ? { after } : {}),
      mediaType: [MediaLibrary.MediaType.photo, MediaLibrary.MediaType.video],
      sortBy: [[MediaLibrary.SortBy.creationTime, false]],
    });
    for (const asset of page.assets) {
      const size = sizeOf(asset.uri);
      if (size === undefined) {
        continue;
      }

      out.push({
        id: asset.id,
        name: asset.filename,
        kind: asset.mediaType === MediaLibrary.MediaType.video ? "video" : "photo",
        size,
        modifiedAt: Math.round(asset.modificationTime),
        albums: asset.albumId && albums.has(asset.albumId) ? [albums.get(asset.albumId)!] : [],
        uri: asset.uri,
      });
    }

    if (!page.hasNextPage) {
      return out;
    }

    after = page.endCursor;
  }
}

/** Whether the phone is on Wi-Fi (or Ethernet), as "only on Wi-Fi" needs. */
export async function onWifi(): Promise<boolean> {
  const state = await Network.getNetworkStateAsync();
  return (
    state.isConnected === true &&
    (state.type === Network.NetworkStateType.WIFI || state.type === Network.NetworkStateType.ETHERNET)
  );
}

/** Whether the phone is charging or full, as "only while charging" needs. */
export async function charging(): Promise<boolean> {
  const state = await Battery.getBatteryStateAsync();
  return state === Battery.BatteryState.CHARGING || state === Battery.BatteryState.FULL;
}

/** A file is read and hashed a part at a time; this much at once. */
const PART = 4 * 1024 * 1024;

/** The content's SHA-256 in hex, a part at a time, never holding the whole file. */
export async function hashAsset(asset: PhoneAsset): Promise<string> {
  const handle = new File(asset.uri).open();
  try {
    const hash = new Sha256();
    for (let at = 0; at < asset.size; at += PART) {
      handle.offset = at;
      hash.update(await handle.readBytes(Math.min(PART, asset.size - at)));
    }

    return toHex(hash.digest());
  } finally {
    handle.close();
  }
}
