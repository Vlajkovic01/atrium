import type { DeviceInfo } from "@atrium/shared";
import Constants from "expo-constants";
import { Platform } from "react-native";

export const appVersion: string = Constants.expoConfig?.version ?? "0.0.0";

/** What this phone tells the server about itself when it signs in; shown in the list of devices. */
export function thisDevice(): DeviceInfo {
  return { name: deviceName(), kind: "mobile", platform: Platform.OS, appVersion };
}

function deviceName(): string {
  if (Platform.OS === "android") {
    const { Brand, Model } = Platform.constants;
    const brand = Brand ? Brand.charAt(0).toUpperCase() + Brand.slice(1) : "";

    // Many makers put their name into the model already.
    return Model.toLowerCase().startsWith(brand.toLowerCase()) ? Model : `${brand} ${Model}`.trim();
  }

  if (Platform.OS === "ios") {
    return Platform.isPad ? "iPad" : "iPhone";
  }

  return "Atrium app";
}
