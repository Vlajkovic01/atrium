import { Redirect } from "expo-router";

import { useServers } from "../src/servers.tsx";

/** Where the app opens: on the server in front, or on the way to connect to one. */
export default function Start() {
  const { active } = useServers();
  return <Redirect href={active ? "/home" : "/welcome"} />;
}
