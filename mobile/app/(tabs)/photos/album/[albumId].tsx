import { useLocalSearchParams } from "expo-router";

import { AlbumScreen } from "../../../../src/photos/AlbumScreen.tsx";

export default function Album() {
  const { albumId } = useLocalSearchParams<{ albumId: string }>();
  return <AlbumScreen albumId={albumId} />;
}
