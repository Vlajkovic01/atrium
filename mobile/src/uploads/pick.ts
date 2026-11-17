import * as DocumentPicker from "expo-document-picker";
import * as ImagePicker from "expo-image-picker";

import { sizeOf, type Picked } from "./source";

/** Where files to upload come from. */
export type PickFrom = "files" | "library" | "photo" | "video";

/** What a picker gave back: files, nothing (cancelled), or no permission for the camera. */
export type PickResult =
  | { readonly kind: "picked"; readonly files: readonly Picked[] }
  | { readonly kind: "cancelled" }
  | { readonly kind: "noCamera" };

const pad = (n: number): string => String(n).padStart(2, "0");

/** A name for something just taken with the camera, as cameras name them: "IMG_20261117_093000.jpg". */
export function cameraName(kind: "photo" | "video", extension: string, now = new Date()): string {
  const stamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}_${pad(now.getHours())}${pad(
    now.getMinutes(),
  )}${pad(now.getSeconds())}`;
  return `${kind === "photo" ? "IMG" : "VID"}_${stamp}.${extension}`;
}

function extensionOf(uri: string, fallback: string): string {
  const match = /\.([a-z0-9]{1,5})(?:\?|$)/i.exec(uri);
  return match?.[1]?.toLowerCase() ?? fallback;
}

/**
 * Lets the person choose files to upload. Every picker hands over a copy
 * in the app's own cache, so the upload can continue after the app was
 * closed. Pictures are taken as they are: no editing and no recompression.
 */
export async function pick(from: PickFrom): Promise<PickResult> {
  if (from === "files") {
    const result = await DocumentPicker.getDocumentAsync({ multiple: true, copyToCacheDirectory: true, type: "*/*" });
    if (result.canceled) {
      return { kind: "cancelled" };
    }

    return {
      kind: "picked",
      files: result.assets.map((asset) => ({
        uri: asset.uri,
        name: asset.name,
        size: asset.size ?? sizeOf(asset.uri, asset.file) ?? 0,
        ...(asset.lastModified ? { lastModified: asset.lastModified } : {}),
      })),
    };
  }

  const options: ImagePicker.ImagePickerOptions = {
    mediaTypes: from === "photo" ? ["images"] : from === "video" ? ["videos"] : ["images", "videos"],
    allowsEditing: false,
    allowsMultipleSelection: from === "library",
    quality: 1,
    exif: false,
  };

  let result: ImagePicker.ImagePickerResult;
  if (from === "library") {
    // The system's photo picker: no permission to read the whole library is needed.
    result = await ImagePicker.launchImageLibraryAsync(options);
  } else {
    const permission = await ImagePicker.requestCameraPermissionsAsync();
    if (!permission.granted) {
      return { kind: "noCamera" };
    }

    result = await ImagePicker.launchCameraAsync(options);
  }

  if (result.canceled) {
    return { kind: "cancelled" };
  }

  return {
    kind: "picked",
    files: result.assets.map((asset) => {
      const isVideo = asset.type === "video";
      const name =
        asset.fileName ?? cameraName(isVideo ? "video" : "photo", extensionOf(asset.uri, isVideo ? "mp4" : "jpg"));
      return { uri: asset.uri, name, size: asset.fileSize ?? sizeOf(asset.uri, asset.file) ?? 0 };
    }),
  };
}
