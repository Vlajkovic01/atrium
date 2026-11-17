import { Redirect } from "expo-router";

import { NotificationsScreen } from "../src/NotificationsScreen.tsx";
import { useServers } from "../src/servers.tsx";

/** The notifications of the server in front, over the tabs. */
export default function Notifications() {
  const { active } = useServers();
  return active ? <NotificationsScreen /> : <Redirect href="/welcome" />;
}
