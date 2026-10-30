#!/usr/bin/env python3
"""
Interactive Python terminal for controlling Anova WiFi devices
Supports both Anova Precision Cookers (APC) and Anova Precision Ovens (APO)
"""

import asyncio
import websockets
import json
import uuid
from datetime import datetime, timedelta, timezone
import sys


class AnovaController:
    def __init__(self):
        self.websocket = None
        self.token = None
        self.devices = []
        self.selected_device = None
        self.device_type = None
        self.message_history = []
        self.listener_task = None
        self.temperature_unit = "C"

    async def connect(self, token):
        """Connect to Anova websocket with authentication"""
        self.token = token
        uri = f"wss://devices.anovaculinary.io?token={token}&supportedAccessories=APC,APO"
        
        try:
            self.websocket = await websockets.connect(uri)
            
            # Start background message listener immediately - it will handle device discovery
            self.listener_task = asyncio.create_task(self.continuous_message_listener())
            
            # Wait for device discovery messages to be captured by background listener
            await self.wait_for_device_discovery()
            return True
        except Exception as e:
            print(f"❌ Connection failed: {e}")
            return False

    async def wait_for_device_discovery(self):
        """Wait for device discovery by checking message history"""
        print("🔍 Discovering devices...")
        
        try:
            # Wait for device discovery messages to appear in history
            timeout = 5
            start_time = asyncio.get_event_loop().time()
            
            while (asyncio.get_event_loop().time() - start_time) < timeout:
                # Check message history for device lists
                for msg in self.message_history:
                    data = msg["data"]
                    
                    if data.get("command") == "EVENT_APC_WIFI_LIST" and "payload" in data:
                        for device in data["payload"]:
                            # Check if device already exists
                            if not any(d["id"] == device["cookerId"] for d in self.devices):
                                self.devices.append({
                                    "id": device["cookerId"],
                                    "name": device.get("name", "Anova Precision Cooker"),
                                    "type": "APC",
                                    "device_type": device.get("type", "unknown")
                                })
                    
                    elif data.get("command") == "EVENT_APO_WIFI_LIST" and "payload" in data:
                        for device in data["payload"]:
                            # Check if device already exists
                            if not any(d["id"] == device["cookerId"] for d in self.devices):
                                self.devices.append({
                                    "id": device["cookerId"],
                                    "name": device.get("name", "Anova Precision Oven"),
                                    "type": "APO",
                                    "device_type": device.get("type", "unknown")
                                })
                
                # If we found devices, break early
                if self.devices:
                    break
                    
                # Wait a bit before checking again
                await asyncio.sleep(0.1)
                    
        except Exception as e:
            print(f"Error waiting for device discovery: {e}")

    async def continuous_message_listener(self):
        """Background task to continuously collect websocket messages"""
        print("🎧 Starting background message listener...")
        last_message_time = datetime.now()
        try:
            while True:
                # Add timeout to detect if no messages are coming
                try:
                    message = await asyncio.wait_for(self.websocket.recv(), timeout=30.0)
                    timestamp = datetime.now().strftime("%H:%M:%S")
                    last_message_time = datetime.now()
                    data = json.loads(message)
                    
                    # Store message with timestamp
                    self.message_history.append({
                        "timestamp": timestamp,
                        "data": data,
                        "raw": message
                    })
                    
                    # Process device discovery messages
                    await self.process_device_discovery(data)
                
                except asyncio.TimeoutError:
                    # No message received in 30 seconds
                    now = datetime.now()
                    time_since_last = (now - last_message_time).total_seconds()
                    print(f"⏰ No websocket messages for {time_since_last:.0f} seconds (listener still active)")
                    continue
                
        except websockets.exceptions.ConnectionClosed:
            print("📡 WebSocket connection closed")
        except Exception as e:
            print(f"❌ Error in message listener: {e}")
            import traceback
            traceback.print_exc()

    async def process_device_discovery(self, data):
        """Process device discovery messages from websocket"""
        if data.get("command") == "EVENT_APC_WIFI_LIST" and "payload" in data:
            for device in data["payload"]:
                # Check if device already exists
                if not any(d["id"] == device["cookerId"] for d in self.devices):
                    self.devices.append({
                        "id": device["cookerId"],
                        "name": device.get("name", "Anova Precision Cooker"),
                        "type": "APC",
                        "device_type": device.get("type", "unknown")
                    })
        
        elif data.get("command") == "EVENT_APO_WIFI_LIST" and "payload" in data:
            for device in data["payload"]:
                # Check if device already exists
                if not any(d["id"] == device["cookerId"] for d in self.devices):
                    self.devices.append({
                        "id": device["cookerId"],
                        "name": device.get("name", "Anova Precision Oven"),
                        "type": "APO",
                        "device_type": device.get("type", "unknown")
                    })

    def display_devices(self):
        """Display discovered devices"""
        if not self.devices:
            print("❌ No devices found. Make sure your devices are connected to WiFi and paired with your account.")
            return False
            
        print("\n📱 Discovered devices:")
        for i, device in enumerate(self.devices, 1):
            print(f"{i}. {device['name']} ({device['type']}) - ID: {device['id']}")
        
        return True

    def select_device(self):
        """Let user select a device"""
        if not self.display_devices():
            return False
            
        try:
            choice = input("\nSelect device number: ").strip()
            device_index = int(choice) - 1
            
            if 0 <= device_index < len(self.devices):
                self.selected_device = self.devices[device_index]
                self.device_type = self.selected_device["type"]
                print(f"✅ Selected: {self.selected_device['name']}")
                return True
            else:
                print("❌ Invalid selection")
                return False
        except ValueError:
            print("❌ Please enter a valid number")
            return False

    async def send_command(self, command_data):
        """Send command to device"""
        try:
            await self.websocket.send(json.dumps(command_data))
            print(f"📤 Sent command: {command_data['command']}")
            print("💡 Response will appear in message stream (option 1)")
        except Exception as e:
            print(f"❌ Error sending command: {e}")

    async def send_command_and_wait_for_response(self, command_data, timeout=10, show_timeout_warning=False):
        """Send command and wait for RESPONSE message"""
        try:
            # Record message count before sending
            initial_count = len(self.message_history)
            
            # Send the command
            await self.websocket.send(json.dumps(command_data))
            print(f"📤 Sent command: {command_data['command']}")
            print("⏳ Waiting for response...")
            
            # Show timeout warning if requested
            if show_timeout_warning:
                print("⚠️ This command can take up to 30 seconds to receive a response")
            
            # Wait for RESPONSE messages with timeout
            start_time = asyncio.get_event_loop().time()
            while (asyncio.get_event_loop().time() - start_time) < timeout:
                # Check for new messages since we sent the command
                if len(self.message_history) > initial_count:
                    new_messages = self.message_history[initial_count:]
                    
                    # Look for RESPONSE commands in new messages
                    for msg in new_messages:
                        data = msg["data"]
                        command = data.get("command", "")
                        
                        # Check if this is a response message (not a state update)
                        if command.startswith("RESPONSE") or \
                           (command.startswith("CMD_") and not command.startswith("CMD_STATE")) or \
                           command == "EVENT_EXPORT_READY":
                            
                            print("\n📨 Response received:")
                            print("-" * 40)
                            self.display_formatted_message(msg)
                            print("-" * 40)
                            return True
                
                await asyncio.sleep(0.1)
            
            # Timeout reached
            print(f"⏰ No response received within {timeout} seconds")
            return False
            
        except Exception as e:
            print(f"❌ Error sending command: {e}")
            return False

    def generate_uuid(self):
        """Generate a UUID for requests"""
        return str(uuid.uuid4())

    async def stop_device(self):
        """Stop cooking on selected device"""
        if self.device_type == "APC":
            command = {
                "command": "CMD_APC_STOP",
                "requestId": self.generate_uuid(),
                "payload": {
                    "cookerId": self.selected_device["id"],
                    "type": self.selected_device["device_type"]
                }
            }
        else:  # APO
            command = {
                "command": "CMD_APO_STOP",
                "payload": {
                    "id": self.selected_device["id"],
                    "type": "CMD_APO_STOP"
                },
                "requestId": self.generate_uuid()
            }
        
        await self.send_command_and_wait_for_response(command)

    async def set_temperature_unit(self):
        """Set temperature unit (C or F)"""
        unit = input("Enter temperature unit (C/F): ").upper().strip()
        if unit not in ["C", "F"]:
            print("❌ Invalid unit. Use C or F")
            return

        if self.device_type == "APC":
            command = {
                "command": "CMD_APC_SET_TEMPERATURE_UNIT",
                "requestId": self.generate_uuid(),
                "payload": {
                    "cookerId": self.selected_device["id"],
                    "type": self.selected_device["device_type"],
                    "unit": unit
                }
            }
        else:  # APO
            command = {
                "command": "CMD_APO_SET_TEMPERATURE_UNIT",
                "payload": {
                    "id": self.selected_device["id"],
                    "payload": {
                        "temperatureUnit": unit
                    },
                    "type": "CMD_APO_SET_TEMPERATURE_UNIT"
                },
                "requestId": self.generate_uuid()
            }
        
        result = await self.send_command_and_wait_for_response(command)
        if result:
            self.temperature_unit = unit
            print(f"✅ Temperature unit preference saved: {unit}")

    async def start_sous_vide_cook(self):
        """Start cooking on APC device"""
        try:
            temp_unit = self.temperature_unit
            max_temp = 95 if temp_unit == "C" else 203
            temp = float(input(f"Enter target temperature (°{temp_unit}, max {max_temp}): "))
            
            if temp_unit == "C" and temp > 95:
                print("❌ Temperature too high. Maximum is 95°C for sous vide.")
                return
            elif temp_unit == "F" and temp > 203:
                print("❌ Temperature too high. Maximum is 203°F for sous vide.")
                return
            
            timer_minutes = float(input("Enter cook time (minutes): "))
            timer_seconds = int(timer_minutes * 60)
            
            temp_celsius = temp if temp_unit == "C" else (temp - 32) * 5/9
            
            command = {
                "command": "CMD_APC_START",
                "requestId": self.generate_uuid(),
                "payload": {
                    "cookerId": self.selected_device["id"],
                    "type": self.selected_device["device_type"],
                    "targetTemperature": temp_celsius,
                    "unit": "C",
                    "timer": timer_seconds
                }
            }
            
            await self.send_command_and_wait_for_response(command)
        except ValueError:
            print("❌ Invalid input. Please enter numeric values.")

    async def start_oven_sous_vide(self):
        """Start sous vide cooking on APO device"""
        try:
            temp_unit = self.temperature_unit
            max_temp = 100 if temp_unit == "C" else 212
            temp = float(input(f"Enter target temperature (°{temp_unit}, max {max_temp}): "))
            
            if temp_unit == "C" and temp > 100:
                print("❌ Temperature too high. Maximum is 100°C for wet bulb sous vide.")
                return
            elif temp_unit == "F" and temp > 212:
                print("❌ Temperature too high. Maximum is 212°F for wet bulb sous vide.")
                return
            
            timer_minutes = float(input("Enter cook time (minutes): "))
            timer_seconds = int(timer_minutes * 60)
            
            temp_celsius = temp if temp_unit == "C" else (temp - 32) * 5/9
            
            command = {
                "command": "CMD_APO_START",
                "payload": {
                    "id": self.selected_device["id"],
                    "payload": {
                        "stages": [
                            {
                                "id": self.generate_uuid(),
                                "do": {
                                    "type": "cook",
                                    "fan": {
                                        "speed": 100
                                    },
                                    "heatingElements": {
                                        "top": {"on": False},
                                        "bottom": {"on": False},
                                        "rear": {"on": True}
                                    },
                                    "exhaustVent": {
                                        "state": "closed"
                                    },
                                    "steamGenerators": {
                                        "mode": "relative-humidity",
                                        "relativeHumidity": {
                                            "setpoint": 100
                                        }
                                    },
                                    "temperatureBulbs": {
                                        "mode": "wet",
                                        "wet": {
                                            "setpoint": {
                                                "celsius": temp_celsius
                                            }
                                        }
                                    },
                                    "timer": {
                                        "initial": timer_seconds
                                    }
                                },
                                "exit": {
                                    "conditions": {
                                        "and": {
                                            "nodes.timer.mode": {
                                                "=": "completed"
                                            }
                                        }
                                    }
                                },
                                "title": "",
                                "description": "",
                                "rackPosition": 3
                            }
                        ],
                        "cookId": self.generate_uuid(),
                        "cookerId": self.selected_device["id"],
                        "cookableId": "",
                        "title": "",
                        "type": self.selected_device["device_type"],
                        "originSource": "api",
                        "cookableType": "manual"
                    },
                    "type": "CMD_APO_START"
                },
                "requestId": self.generate_uuid()
            }
            
            await self.send_command_and_wait_for_response(command)
        except ValueError:
            print("❌ Invalid input. Please enter numeric values.")

    async def start_oven_sous_vide_v1(self):
        """Start sous vide cooking on APO v1 device"""
        try:
            temp_f = float(input("Enter target temperature (°F, max 212): "))
            if temp_f > 212:
                print("❌ Temperature too high. Maximum is 212°F for wet bulb sous vide.")
                return
            temp_c = (temp_f - 32) * 5/9
            
            command = {
                "command": "CMD_APO_START",
                "payload": {
                    "id": self.selected_device["id"],
                    "payload": {
                        "cookId": self.generate_uuid(),
                        "stages": [
                            {
                                "stepType": "stage",
                                "id": self.generate_uuid(),
                                "title": "",
                                "description": "",
                                "type": "preheat",
                                "userActionRequired": False,
                                "temperatureBulbs": {
                                    "mode": "wet",
                                    "wet": {
                                        "setpoint": {
                                            "celsius": temp_c,
                                            "fahrenheit": temp_f
                                        }
                                    }
                                },
                                "heatingElements": {
                                    "top": {"on": False},
                                    "bottom": {"on": False},
                                    "rear": {"on": True}
                                },
                                "fan": {"speed": 100},
                                "vent": {"open": False},
                                "rackPosition": 3,
                                "stageTransitionType": "automatic",
                                "steamGenerators": {
                                    "mode": "relative-humidity",
                                    "relativeHumidity": {"setpoint": 100}
                                }
                            },
                            {
                                "stepType": "stage",
                                "id": self.generate_uuid(),
                                "title": "",
                                "description": "",
                                "type": "cook",
                                "userActionRequired": False,
                                "temperatureBulbs": {
                                    "mode": "wet",
                                    "wet": {
                                        "setpoint": {
                                            "celsius": temp_c,
                                            "fahrenheit": temp_f
                                        }
                                    }
                                },
                                "heatingElements": {
                                    "top": {"on": False},
                                    "bottom": {"on": False},
                                    "rear": {"on": True}
                                },
                                "fan": {"speed": 100},
                                "vent": {"open": False},
                                "rackPosition": 3,
                                "stageTransitionType": "automatic",
                                "steamGenerators": {
                                    "mode": "relative-humidity",
                                    "relativeHumidity": {"setpoint": 100}
                                }
                            }
                        ]
                    },
                    "type": "CMD_APO_START"
                },
                "requestId": self.generate_uuid()
            }
            
            await self.send_command_and_wait_for_response(command)
        except ValueError:
            print("❌ Invalid input. Please enter numeric values.")

    async def start_oven_roast(self):
        """Start roasting on APO device"""
        try:
            temp_unit = self.temperature_unit
            max_temp = 250 if temp_unit == "C" else 482
            temp = float(input(f"Enter roasting temperature (°{temp_unit}, max {max_temp}): "))
            
            if temp_unit == "C" and temp > 250:
                print("❌ Temperature too high. Maximum is 250°C for roasting.")
                return
            elif temp_unit == "F" and temp > 482:
                print("❌ Temperature too high. Maximum is 482°F for roasting.")
                return
            
            timer_minutes = float(input("Enter cook time (minutes): "))
            timer_seconds = int(timer_minutes * 60)
            
            temp_celsius = temp if temp_unit == "C" else (temp - 32) * 5/9
            
            command = {
                "command": "CMD_APO_START",
                "payload": {
                    "id": self.selected_device["id"],
                    "payload": {
                        "stages": [
                            {
                                "id": self.generate_uuid(),
                                "do": {
                                    "type": "cook",
                                    "fan": {
                                        "speed": 75
                                    },
                                    "heatingElements": {
                                        "top": {"on": False},
                                        "bottom": {"on": True},
                                        "rear": {"on": True}
                                    },
                                    "exhaustVent": {
                                        "state": "closed"
                                    },
                                    "temperatureBulbs": {
                                        "mode": "dry",
                                        "dry": {
                                            "setpoint": {
                                                "celsius": temp_celsius
                                            }
                                        }
                                    },
                                    "timer": {
                                        "initial": timer_seconds
                                    }
                                },
                                "exit": {
                                    "conditions": {
                                        "and": {
                                            "nodes.timer.mode": {
                                                "=": "completed"
                                            }
                                        }
                                    }
                                },
                                "title": "",
                                "description": "",
                                "rackPosition": 3
                            }
                        ],
                        "cookId": self.generate_uuid(),
                        "cookerId": self.selected_device["id"],
                        "cookableId": "",
                        "title": "",
                        "type": self.selected_device["device_type"],
                        "originSource": "api",
                        "cookableType": "manual"
                    },
                    "type": "CMD_APO_START"
                },
                "requestId": self.generate_uuid()
            }
            
            await self.send_command_and_wait_for_response(command)
        except ValueError:
            print("❌ Invalid input. Please enter numeric values.")

    async def start_oven_roast_v1(self):
        """Start roasting on APO v1 device"""
        try:
            temp_f = float(input("Enter roasting temperature (°F, max 482): "))
            if temp_f > 482:
                print("❌ Temperature too high. Maximum is 482°F for roasting.")
                return
            temp_c = (temp_f - 32) * 5/9
            
            command = {
                "command": "CMD_APO_START",
                "payload": {
                    "id": self.selected_device["id"],
                    "payload": {
                        "cookId": self.generate_uuid(),
                        "stages": [
                            {
                                "stepType": "stage",
                                "id": self.generate_uuid(),
                                "title": "",
                                "description": "",
                                "type": "preheat",
                                "userActionRequired": False,
                                "temperatureBulbs": {
                                    "mode": "dry",
                                    "dry": {
                                        "setpoint": {
                                            "celsius": temp_c,
                                            "fahrenheit": temp_f
                                        }
                                    }
                                },
                                "heatingElements": {
                                    "top": {"on": False},
                                    "bottom": {"on": True},
                                    "rear": {"on": True}
                                },
                                "fan": {"speed": 75},
                                "vent": {"open": False},
                                "rackPosition": 3,
                                "stageTransitionType": "automatic"
                            },
                            {
                                "stepType": "stage",
                                "id": self.generate_uuid(),
                                "title": "",
                                "description": "",
                                "type": "cook",
                                "userActionRequired": False,
                                "temperatureBulbs": {
                                    "mode": "dry",
                                    "dry": {
                                        "setpoint": {
                                            "celsius": temp_c,
                                            "fahrenheit": temp_f
                                        }
                                    }
                                },
                                "heatingElements": {
                                    "top": {"on": False},
                                    "bottom": {"on": True},
                                    "rear": {"on": True}
                                },
                                "fan": {"speed": 75},
                                "vent": {"open": False},
                                "rackPosition": 3,
                                "stageTransitionType": "automatic"
                            }
                        ]
                    },
                    "type": "CMD_APO_START"
                },
                "requestId": self.generate_uuid()
            }
            
            await self.send_command_and_wait_for_response(command)
        except ValueError:
            print("❌ Invalid input. Please enter numeric values.")

    async def start_oven_steam(self):
        """Start steam cooking on APO device"""
        try:
            temp_unit = self.temperature_unit
            max_temp = 250 if temp_unit == "C" else 482
            temp = float(input(f"Enter temperature (°{temp_unit}, max {max_temp}): "))
            
            if temp_unit == "C" and temp > 250:
                print("❌ Temperature too high. Maximum is 250°C for steam cooking.")
                return
            elif temp_unit == "F" and temp > 482:
                print("❌ Temperature too high. Maximum is 482°F for steam cooking.")
                return
            
            humidity = int(input("Enter humidity percentage (0-100): "))
            if humidity < 0 or humidity > 100:
                print("❌ Humidity must be between 0-100%.")
                return
            timer_minutes = float(input("Enter cook time (minutes): "))
            timer_seconds = int(timer_minutes * 60)
            
            temp_celsius = temp if temp_unit == "C" else (temp - 32) * 5/9
            
            command = {
                "command": "CMD_APO_START",
                "payload": {
                    "id": self.selected_device["id"],
                    "payload": {
                        "stages": [
                            {
                                "id": self.generate_uuid(),
                                "do": {
                                    "type": "cook",
                                    "fan": {
                                        "speed": 100
                                    },
                                    "heatingElements": {
                                        "top": {"on": False},
                                        "bottom": {"on": True},
                                        "rear": {"on": True}
                                    },
                                    "exhaustVent": {
                                        "state": "closed"
                                    },
                                    "temperatureBulbs": {
                                        "mode": "dry",
                                        "dry": {
                                            "setpoint": {
                                                "celsius": temp_celsius
                                            }
                                        }
                                    },
                                    "steamGenerators": {
                                        "mode": "relative-humidity",
                                        "relativeHumidity": {
                                            "setpoint": humidity
                                        }
                                    },
                                    "timer": {
                                        "initial": timer_seconds
                                    }
                                },
                                "exit": {
                                    "conditions": {
                                        "and": {
                                            "nodes.timer.mode": {
                                                "=": "completed"
                                            }
                                        }
                                    }
                                },
                                "title": "",
                                "description": "",
                                "rackPosition": 3
                            }
                        ],
                        "cookId": self.generate_uuid(),
                        "cookerId": self.selected_device["id"],
                        "cookableId": "",
                        "title": "",
                        "type": self.selected_device["device_type"],
                        "originSource": "api",
                        "cookableType": "manual"
                    },
                    "type": "CMD_APO_START"
                },
                "requestId": self.generate_uuid()
            }
            
            await self.send_command_and_wait_for_response(command)
        except ValueError:
            print("❌ Invalid input. Please enter numeric values.")

    async def start_oven_steam_v1(self):
        """Start steam cooking on APO v1 device"""
        try:
            temp_f = float(input("Enter temperature (°F, max 482): "))
            if temp_f > 482:
                print("❌ Temperature too high. Maximum is 482°F for steam cooking.")
                return
            temp_c = (temp_f - 32) * 5/9
            humidity = int(input("Enter humidity percentage (0-100): "))
            if humidity < 0 or humidity > 100:
                print("❌ Humidity must be between 0-100%.")
                return
            
            command = {
                "command": "CMD_APO_START", 
                "payload": {
                    "id": self.selected_device["id"],
                    "payload": {
                        "cookId": self.generate_uuid(),
                        "stages": [
                            {
                                "stepType": "stage",
                                "id": self.generate_uuid(),
                                "title": "",
                                "description": "",
                                "type": "preheat",
                                "userActionRequired": False,
                                "temperatureBulbs": {
                                    "mode": "dry",
                                    "dry": {
                                        "setpoint": {
                                            "celsius": temp_c,
                                            "fahrenheit": temp_f
                                        }
                                    }
                                },
                                "heatingElements": {
                                    "top": {"on": True},
                                    "bottom": {"on": True},
                                    "rear": {"on": True}
                                },
                                "fan": {"speed": 50},
                                "vent": {"open": False},
                                "rackPosition": 3,
                                "stageTransitionType": "automatic",
                                "steamGenerators": {
                                    "mode": "relative-humidity",
                                    "relativeHumidity": {"setpoint": humidity}
                                }
                            },
                            {
                                "stepType": "stage",
                                "id": self.generate_uuid(),
                                "title": "",
                                "description": "",
                                "type": "cook",
                                "userActionRequired": False,
                                "temperatureBulbs": {
                                    "mode": "dry",
                                    "dry": {
                                        "setpoint": {
                                            "celsius": temp_c,
                                            "fahrenheit": temp_f
                                        }
                                    }
                                },
                                "heatingElements": {
                                    "top": {"on": True},
                                    "bottom": {"on": True},
                                    "rear": {"on": True}
                                },
                                "fan": {"speed": 50},
                                "vent": {"open": False},
                                "rackPosition": 3,
                                "stageTransitionType": "automatic",
                                "steamGenerators": {
                                    "mode": "relative-humidity",
                                    "relativeHumidity": {"setpoint": humidity}
                                }
                            }
                        ]
                    },
                    "type": "CMD_APO_START"
                },
                "requestId": self.generate_uuid()
            }
            
            await self.send_command_and_wait_for_response(command)
        except ValueError:
            print("❌ Invalid input. Please enter numeric values.")

    async def export_telemetry(self):
        """Export telemetry data from device"""
        print("📊 Export telemetry data")
        print("Note: Date range cannot exceed 14 days, and start date cannot be more than 90 days in the past")
        
        try:
            start_date = input("Enter start date (YYYY-MM-DD): ").strip()
            end_date = input("Enter end date (YYYY-MM-DD): ").strip()
            
            # Validate date format
            datetime.strptime(start_date, "%Y-%m-%d")
            datetime.strptime(end_date, "%Y-%m-%d")
            
            command = {
                "command": "CMD_EXPORT_TELEMETRY",
                "requestId": self.generate_uuid(),
                "payload": {
                    "id": self.generate_uuid(),
                    "type": "CMD_EXPORT_TELEMETRY",
                    "payload": {
                        "deviceId": self.selected_device["id"],
                        "startTime": start_date,
                        "endTime": end_date
                    }
                }
            }
            
            await self.send_command_and_wait_for_response(command, timeout=30, show_timeout_warning=True)
        except ValueError:
            print("❌ Invalid date format. Use YYYY-MM-DD")

    async def show_message_stream(self):
        print("Message History")
        print("=" * 60)
        
        # Show recent message history first
        if self.message_history:
            recent_messages = self.message_history[-10:]  # Show last 10 messages
            for msg in recent_messages:
                self.display_formatted_message(msg)
        
        print("=" * 60)
        print("🔴 LIVE - New messages will appear below - Press Enter to return to menu")
        
        # Track the last message count to detect new messages
        last_count = len(self.message_history)
        
        # Create concurrent tasks for message monitoring and user input
        async def monitor_messages():
            nonlocal last_count
            while True:
                current_count = len(self.message_history)
                if current_count > last_count:
                    # Display new messages
                    new_messages = self.message_history[last_count:]
                    for msg in new_messages:
                        self.display_formatted_message(msg)
                    last_count = current_count
                await asyncio.sleep(0.1)
        
        async def wait_for_user_input():
            loop = asyncio.get_event_loop()
            # Use run_in_executor to make input() non-blocking
            await loop.run_in_executor(None, input)
        
        # Run both tasks concurrently, return when user input is received
        monitor_task = asyncio.create_task(monitor_messages())
        input_task = asyncio.create_task(wait_for_user_input())
        
        try:
            # Wait for user to press Enter
            await input_task
        except KeyboardInterrupt:
            pass
        finally:
            # Cancel the monitoring task
            monitor_task.cancel()
            try:
                await monitor_task
            except asyncio.CancelledError:
                pass
        
        print("Exiting message stream...")

    def display_formatted_message(self, msg):
        """Format and display a single message"""
        timestamp = msg["timestamp"]
        data = msg["data"]
        command = data.get("command", "UNKNOWN")
        
        # Format different message types
        if command.startswith("EVENT_"):
            if "STATE" in command:
                # State messages - show full state
                payload = data.get("payload", {})
                if isinstance(payload, dict):
                    temp = payload.get("temperature", "N/A")
                    status = payload.get("status", payload.get("state", "N/A"))
                    if temp != "N/A" and isinstance(temp, (int, float)):
                        if self.temperature_unit == "F":
                            temp_display = f"{temp * 9/5 + 32:.1f}°F"
                        else:
                            temp_display = f"{temp}°C"
                    else:
                        temp_display = "N/A"
                    print(f"[{timestamp}] STATE: Temp: {temp_display}, Status: {status}")
                else:
                    print(f"[{timestamp}] {command}: {payload}")
            else:
                # Other events
                print(f"[{timestamp}] EVENT: {command}")
        elif command.startswith("RESPONSE"):
            # Command responses - show full response
            payload = data.get("payload", {})
            print(f"[{timestamp}] RESPONSE: {command} - {payload}")
        elif command.startswith("CMD_"):
            # Command responses - show full response
            payload = data.get("payload", {})
            print(f"[{timestamp}] RESPONSE: {command} - {payload}")
        else:
            # Unknown format - show raw
            print(f"[{timestamp}] RAW: {command}")
        
        # Add newline between messages
        print()

    async def show_menu(self):
        """Show command menu based on device type"""
        if self.device_type == "APC":
            print(f"\n🍳 Commands for {self.selected_device['name']} (Sous Vide):")
            print("1. Show message stream")
            print("2. Start sous vide cook")
            print("3. Stop cooking")
            print("4. Set temperature unit")
            print("5. Export telemetry data")
            print("0. Exit")
        else:  # APO
            print(f"\n🔥 Commands for {self.selected_device['name']} (Oven):")
            print("1. Show message stream")
            print("2. Start sous vide cook (wet bulb)")
            print("3. Start roasting (dry bulb)")
            print("4. Start steam cooking")
            print("5. Stop cooking")
            print("6. Set temperature unit") 
            print("7. Export telemetry data")
            print("0. Exit")

    async def handle_menu_choice(self, choice):
        """Handle user menu selection"""
        if self.device_type == "APC":
            if choice == "1":
                await self.show_message_stream()
                return "no_pause"  # Special return value to skip "Press Enter"
            elif choice == "2":
                await self.start_sous_vide_cook()
            elif choice == "3":
                await self.stop_device()
            elif choice == "4":
                await self.set_temperature_unit()
            elif choice == "5":
                await self.export_telemetry()
            elif choice == "0":
                return False
            else:
                print("❌ Invalid choice")
        else:  # APO
            if choice == "1":
                await self.show_message_stream()
                return "no_pause"  # Special return value to skip "Press Enter"
            elif choice == "2":
                # Use v1 or v2 commands based on device_type
                if self.selected_device["device_type"] == "oven_v2":
                    await self.start_oven_sous_vide()
                else:
                    await self.start_oven_sous_vide_v1()
            elif choice == "3":
                # Use v1 or v2 commands based on device_type
                if self.selected_device["device_type"] == "oven_v2":
                    await self.start_oven_roast()
                else:
                    await self.start_oven_roast_v1()
            elif choice == "4":
                # Use v1 or v2 commands based on device_type
                if self.selected_device["device_type"] == "oven_v2":
                    await self.start_oven_steam()
                else:
                    await self.start_oven_steam_v1()
            elif choice == "5":
                await self.stop_device()
            elif choice == "6":
                await self.set_temperature_unit()
            elif choice == "7":
                await self.export_telemetry()
            elif choice == "0":
                return False
            else:
                print("❌ Invalid choice")
        
        return True

    async def run_interactive_menu(self):
        """Run the interactive menu loop"""
        try:
            while True:
                await self.show_menu()
                choice = input("\nSelect option: ").strip()
                
                result = await self.handle_menu_choice(choice)
                if result == False:
                    print("👋 Exiting...")
                    break
                elif result == "no_pause":
                    # Skip the "Press Enter to continue" for message stream
                    continue
                else:
                    # Normal pause for other commands
                    input("\nPress Enter to continue...")
        except KeyboardInterrupt:
            print("\n👋 Interrupted by user")
        except Exception as e:
            print(f"❌ Menu error: {e}")
        finally:
            # Ensure cleanup happens
            await self.close()

    async def close(self):
        """Close websocket connection"""
        print("🔄 Cleaning up connections...")
        
        # Cancel background listener task
        if self.listener_task and not self.listener_task.done():
            print("🛑 Stopping background listener...")
            self.listener_task.cancel()
            try:
                await asyncio.wait_for(self.listener_task, timeout=2.0)
            except (asyncio.CancelledError, asyncio.TimeoutError):
                pass
        
        # Close websocket connection
        if self.websocket:
            print("🔌 Closing websocket...")
            try:
                await self.websocket.close()
            except Exception as e:
                print(f"⚠️ Error closing websocket: {e}")
        
        print("✅ Cleanup complete")


async def main():
    print("🔥 Anova WiFi Device Controller")
    print("=" * 40)
    
    # Get Personal Access Token
    print("\n🔑 Personal Access Token Required")
    print("Find your token in the Anova Oven app: More → Developer → Personal Access Tokens")
    print("(Note: Sous Vide users should also download the Oven app to generate tokens)")
    
    token = input("\nEnter your Personal Access Token (starts with 'anova-'): ").strip()
    
    if not token.startswith("anova-"):
        print("❌ Invalid token format. Token should start with 'anova-'")
        return
    
    controller = AnovaController()
    
    try:
        # Connect to websocket
        if not await controller.connect(token):
            return
        
        # Select device
        if not controller.select_device():
            return
        
        print(f"\n🚀 Ready to control your {controller.selected_device['name']}!")
        print("Keep this connection open to send commands...")
        
        # Run interactive menu (background listener already started)
        await controller.run_interactive_menu()
        
    except KeyboardInterrupt:
        print("\n👋 Goodbye!")
    except Exception as e:
        print(f"❌ Error: {e}")
    finally:
        # Cleanup will be handled by run_interactive_menu
        pass


if __name__ == "__main__":
    # Check if websockets is available
    try:
        import websockets
    except ImportError:
        print("❌ Missing required dependency: websockets")
        print("Install with: pip install websockets")
        sys.exit(1)
    
    asyncio.run(main())
