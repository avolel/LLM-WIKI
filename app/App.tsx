import { useState } from 'react';
import { StyleSheet, View } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import { AppProvider } from './src/state';
import { TabBar } from './src/components/TabBar';
import { TABS, type RouteName } from './src/navigation';
import { ChatScreen } from './src/screens/ChatScreen';
import { BrowseScreen } from './src/screens/BrowseScreen';
import { ProjectsScreen } from './src/screens/ProjectsScreen';
import { HomeScreen } from './src/screens/HomeScreen';
import { theme } from './src/theme';

// Phase 8: a custom useState tab switcher (no navigation library) over the four screens,
// wrapped in AppProvider so every screen can read/set the active project.
export default function App() {
  const [tab, setTab] = useState<RouteName>('Chat');
  return (
    <AppProvider>
      <View style={styles.container}>
        <View style={styles.body}>
          {tab === 'Chat' && <ChatScreen />}
          {tab === 'Browse' && <BrowseScreen />}
          {tab === 'Projects' && <ProjectsScreen />}
          {tab === 'Status' && <HomeScreen />}
        </View>
        <TabBar tabs={TABS} active={tab} onSelect={setTab} />
        <StatusBar style="auto" />
      </View>
    </AppProvider>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: theme.colors.background },
  body: { flex: 1 },
});
