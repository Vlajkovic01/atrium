import { Redirect } from "expo-router";

import { SearchScreen } from "../src/photos/SearchScreen.tsx";
import { useServers } from "../src/servers.tsx";

/** Search across the server in front, over the tabs. */
export default function Search() {
  const { active } = useServers();
  return active ? <SearchScreen /> : <Redirect href="/welcome" />;
}
