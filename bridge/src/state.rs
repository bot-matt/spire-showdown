use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum DuelState {
    Idle,
    Preflighting,
    Ready,
    Launching,
    Connecting,
    Playing,
    Completed,
    Failed,
    Cancelled,
}

impl DuelState {
    pub fn can_transition_to(self, next: Self) -> bool {
        use DuelState::*;
        match (self, next) {
            (Idle, Preflighting)
            | (Preflighting, Ready)
            | (Ready, Launching)
            | (Launching, Connecting)
            | (Connecting, Playing)
            | (Playing, Completed)
            | (Completed | Failed | Cancelled, Idle) => true,
            (state, Failed | Cancelled)
                if !matches!(state, Idle | Completed | Failed | Cancelled) =>
            {
                true
            }
            _ => false,
        }
    }
}

#[derive(Debug)]
pub struct DuelMachine {
    state: DuelState,
}

impl Default for DuelMachine {
    fn default() -> Self {
        Self {
            state: DuelState::Idle,
        }
    }
}

impl DuelMachine {
    pub fn state(&self) -> DuelState {
        self.state
    }

    pub fn transition(&mut self, next: DuelState) -> Result<(), String> {
        if self.state.can_transition_to(next) {
            self.state = next;
            Ok(())
        } else {
            Err(format!(
                "invalid duel transition: {:?} -> {:?}",
                self.state, next
            ))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn happy_path_reaches_completed_and_resets() {
        let mut machine = DuelMachine::default();
        for state in [
            DuelState::Preflighting,
            DuelState::Ready,
            DuelState::Launching,
            DuelState::Connecting,
            DuelState::Playing,
            DuelState::Completed,
            DuelState::Idle,
        ] {
            machine.transition(state).unwrap();
        }
        assert_eq!(machine.state(), DuelState::Idle);
    }

    #[test]
    fn active_duel_can_fail_safely() {
        let mut machine = DuelMachine::default();
        machine.transition(DuelState::Preflighting).unwrap();
        machine.transition(DuelState::Failed).unwrap();
        machine.transition(DuelState::Idle).unwrap();
    }

    #[test]
    fn cannot_skip_readiness() {
        let mut machine = DuelMachine::default();
        assert!(machine.transition(DuelState::Playing).is_err());
    }
}
