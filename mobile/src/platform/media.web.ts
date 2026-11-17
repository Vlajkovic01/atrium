import { sha256, toHex, type DeviceAsset } from "@atrium/shared";

// Only for looking at the app's screens in a browser while developing
// (mobile/preview): a small made-up camera roll of pictures drawn on a
// canvas, so the backup screens and the uploads can be tried end to end.

export type MediaAccess = "all" | "limited" | "none" | "denied";

let granted = false;

export function mediaAccess(): Promise<MediaAccess> {
  return Promise.resolve(granted ? "all" : "none");
}

export function askMediaAccess(): Promise<MediaAccess> {
  granted = true;
  return Promise.resolve("all");
}

export function deviceAlbums(): Promise<readonly { readonly id: string; readonly title: string }[]> {
  return Promise.resolve([
    { id: "camera", title: "Camera" },
    { id: "screenshots", title: "Screenshots" },
  ]);
}

export interface PhoneAsset extends DeviceAsset {
  readonly uri: string;
}

function picture(seed: number): string {
  const canvas = document.createElement("canvas");
  canvas.width = 64;
  canvas.height = 48;
  const context = canvas.getContext("2d")!;
  context.fillStyle = `hsl(${(seed * 47) % 360} 60% 50%)`;
  context.fillRect(0, 0, 64, 48);
  context.fillStyle = "#fff";
  context.fillText(String(seed), 8, 28);
  return canvas.toDataURL("image/png");
}

let roll: PhoneAsset[] | null = null;

export function listAssets(): Promise<PhoneAsset[]> {
  roll ??= Array.from({ length: 6 }, (_, i) => {
    const uri = picture(i + 1);
    return {
      id: `asset-${i + 1}`,
      name: i < 4 ? `IMG_2026111${i}.png` : `Screenshot_${i}.png`,
      kind: "photo" as const,
      size: atob(uri.split(",")[1]!).length,
      modifiedAt: Date.UTC(2026, 10, 10 + i),
      albums: [i < 4 ? "Camera" : "Screenshots"],
      uri,
    };
  });
  return Promise.resolve(roll);
}

export function onWifi(): Promise<boolean> {
  return Promise.resolve(true);
}

export function charging(): Promise<boolean> {
  return Promise.resolve(true);
}

export async function hashAsset(asset: PhoneAsset): Promise<string> {
  return toHex(sha256(new Uint8Array(await (await fetch(asset.uri)).arrayBuffer())));
}
