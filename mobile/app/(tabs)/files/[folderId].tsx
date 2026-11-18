import { useLocalSearchParams } from "expo-router";

import { FolderScreen } from "../../../src/files/FolderScreen.tsx";

export default function Folder() {
  const { folderId } = useLocalSearchParams<{ folderId: string }>();
  return <FolderScreen folderId={folderId} />;
}
