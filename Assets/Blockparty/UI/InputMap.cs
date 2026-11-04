// INPUT-ACCESS@1: the one live input map over Accessibility (BP-PRES-05).
// Every action resolves through the shared Accessibility bindings, so a
// remap updates routing and hints immediately. No action requires a hold:
// HoldActions is empty, so one-hand play needs no chord or sustained press.
// Menus open/close only on their actions and never expire on game time.
// Plain C# 9, no UnityEngine dependency. Fast Enter Play Mode: instance state.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.UI
{
    public sealed class InputMap
    {
        private readonly Accessibility accessibility;
        private readonly Clock clock;
        private string controlState;
        private bool menuOpen;

        public InputMap(Accessibility accessibility, Clock clock)
        {
            if (accessibility == null)
            {
                throw new ArgumentNullException("accessibility");
            }
            if (clock == null)
            {
                throw new ArgumentNullException("clock");
            }
            this.accessibility = accessibility;
            this.clock = clock;
            controlState = "Camera";
        }

        public IEnumerable<string> Actions
        {
            get { return InputActions.Names; }
        }

        public IEnumerable<string> HoldActions
        {
            get { return new string[0]; }
        }

        public string ControlState
        {
            get { return controlState; }
        }

        public bool MenuOpen
        {
            get { return menuOpen; }
        }

        public void EnterState(string state)
        {
            if (!string.IsNullOrWhiteSpace(state))
            {
                controlState = state;
            }
        }

        public bool TryActivate(string activeBinding)
        {
            string action = accessibility.ResolveInput(activeBinding);
            if (action.Length == 0)
            {
                return false;
            }
            if (action == "Pause")
            {
                if (clock.Paused)
                {
                    clock.Resume();
                }
                else
                {
                    clock.Pause();
                }
            }
            else if (action == "OpenActionMenu")
            {
                menuOpen = true;
            }
            else if (action == "CloseActionMenu")
            {
                menuOpen = false;
            }
            return true;
        }

        // Menus have no expiry: elapsed game time never dismisses them.
        public void AdvanceGameSeconds(double seconds)
        {
        }

        public string HintFor(string action)
        {
            return accessibility.BindingFor(action);
        }

        public bool RequiresHold(string action)
        {
            return false;
        }

        public string ToggleAlternativeFor(string holdAction)
        {
            return string.Empty;
        }

        public bool IsActive(string action)
        {
            return false;
        }
    }
}
