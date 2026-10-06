from flask import Flask, request, jsonify
import json
import os
import math
import sys

app = Flask(__name__)

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(SCRIPT_DIR, 'config.json')
try:
    with open(CONFIG_PATH, 'r') as f:
        CONFIG = json.load(f)
except FileNotFoundError:
    print("Error: config.json not found. Using default internal values.")
    CONFIG = {
        "physics_thresholds": {
            "base_confidence": 0.5, "ignore_threshold": 0.3, 
            "anomaly_std_dev_multiplier": 2
        },
        "rules_data": {
            }
    }

TAG_COLLIDER_MAP_PATH = os.path.join(SCRIPT_DIR, "TagColliderMap.json")
THRESHOLDS = CONFIG['physics_thresholds']
RULES = CONFIG['rules_data'] 

BASE_CONFIDENCE = THRESHOLDS['base_confidence']
IGNORE_THRESHOLD = THRESHOLDS['ignore_threshold']
MAD_SCALE_FACTOR = 1.4826 


def calculate_mode(data_list, precision=2):
    if not data_list:
        return 1.0
    
    rounded_data = [round(x, precision) for x in data_list]
    
    counts = {}
    for value in rounded_data:
        counts[value] = counts.get(value, 0) + 1
        
    if not counts:
        return 1.0
        
    mode_value = max(counts, key=counts.get)
    return mode_value

def calculate_median(data_list):
    if not data_list:
        return 0.0
    
    sorted_data = sorted(data_list)
    n = len(sorted_data)
    
    if n % 2 == 1:
        return sorted_data[n // 2]
    else:
        mid1 = sorted_data[n // 2 - 1]
        mid2 = sorted_data[n // 2]
        return (mid1 + mid2) / 2

def calculate_mad(data_list, median_val):
    if not data_list:
        return 0.0
    
    deviations = [abs(x - median_val) for x in data_list]
    
    return calculate_median(deviations)

def calculate_stats(values):
    if not values:
        return 0, 0
    mean = sum(values) / len(values)
    variance = sum((x - mean) ** 2 for x in values) / len(values)
    std_dev = math.sqrt(variance)
    return mean, std_dev


def has_rigidbody(obj):
    component_names = obj.get('componentNames', [])
    if any("Rigidbody" in name for name in component_names):
        return True
    current_mass = obj.get('mass', 1.0)
    if abs(current_mass - 1.0) > 1e-6:
        return True
    if obj.get('isKinematic', False) or obj.get('useGravity', False):
        return True
    return False


def check_missing_rigidbody(obj, tag_is_static, obj_has_rb, tag_info):
    best_coll_type = tag_info.get('colliderType', 'None')
    
    is_bug = (
        not tag_is_static and 
        not obj_has_rb and 
        best_coll_type != "None"
    )
    
    if is_bug:
        bug_type = "Missing Rigidbody"
        fix_action = "ADD_RIGIDBODY"
        suggestion = f"Object is marked as non-static and the Tag Collider Map recommends a {best_coll_type} Collider, but it lacks a Rigidbody, which is required for physics interaction."
        debug_info_str = f"Object Rigidbody: None\nMap Static: False\nMap Recommended Collider: {best_coll_type}"
        return {'bug_type': bug_type, 'suggestion': suggestion, 'fix_action': fix_action, 'debug': debug_info_str}
    return None

def check_mismatch_collider(obj, tag_collider_map):
    object_tag = obj.get('objectTag')
    coll_type = obj.get('colliderType', 'None')
    
    if coll_type == "":
        coll_type = "None"

    if coll_type == "None":
        return None

    if object_tag in tag_collider_map:
        tag_info = tag_collider_map[object_tag]
        best_coll_type = tag_info.get('colliderType', 'None')
        is_mismatch = coll_type != best_coll_type
        
        if is_mismatch:
            bug_type = "Mismatch Collider"
            fix_action = f"REPLACE_COLLIDER:{best_coll_type}"
            suggestion = f"The Tag-Map recommends a {best_coll_type} Collider for tag '{object_tag}', but object uses a {coll_type} Collider."
            debug_info_str = f"Object Collider: {coll_type}\nMap Recommended: {best_coll_type}"
            return {'bug_type': bug_type, 'suggestion': suggestion, 'fix_action': fix_action, 'tag_info': tag_info, 'debug': debug_info_str}
    return None

def check_mesh_convex_collider_bug(obj, obj_has_rb):
    bug_type = "Mesh Convex Collider Bug"
    is_bug = obj_has_rb and obj.get('colliderType') == 'Mesh' and not obj.get('isMeshColliderConvex', False)
    
    if is_bug:
        fix_action = "MESH_CONVEX"
        suggestion = "RL suggests enabling 'Convex' on Mesh Collider for dynamic props."
        return {'bug_type': bug_type, 'suggestion': suggestion, 'fix_action': fix_action}
    return None

def check_kinematic_gravity(obj, obj_has_rb):
    bug_type = "Kinematic with Gravity"
    is_bug = obj_has_rb and obj.get('isKinematic') and obj.get('useGravity')
    
    if is_bug:
        fix_action = "DISABLE_GRAVITY"
        suggestion = "Rigidbody is Kinematic (script controlled) but has Gravity enabled. This usually results in unexpected behavior."
        return {'bug_type': bug_type, 'suggestion': suggestion, 'fix_action': fix_action}
    return None

def check_missing_collider(obj, obj_has_collider, tag_collider_map, tag_info):
    if obj_has_collider:
        return None

    bug_type = "Missing Collider"
    object_tag = obj.get('objectTag')
    if object_tag in tag_collider_map and tag_info.get('colliderType') == "None":
        return None

    obj_type = obj.get('objectType', 'Unknown')
    recommended_collider_type = "Mesh"
    fix_action = "ADD_MESH_COLLIDER" 

    if object_tag in tag_collider_map and tag_info.get('colliderType') != "None":
        recommended_collider_type = tag_info['colliderType']
        fix_action = f"ADD_{recommended_collider_type.upper()}_COLLIDER"
    elif obj_type == "Cube":
        recommended_collider_type = "Box"
        fix_action = "ADD_BOX_COLLIDER"
    elif obj_type == "Sphere":
        recommended_collider_type = "Sphere"
        fix_action = "ADD_SPHERE_COLLIDER"
    elif obj_type in ("Capsule", "Cylinder"):
        recommended_collider_type = "Capsule"
        fix_action = "ADD_CAPSULE_COLLIDER"
        
    suggestion = f"Object is missing a Collider component. Recommending a {recommended_collider_type} Collider based on its type or Tag '{object_tag}'."
    debug_info_str = f"Object Collider: None\nRecommended Collider: {recommended_collider_type}"

    return {
        'bug_type': bug_type, 
        'suggestion': suggestion, 
        'fix_action': fix_action,
        'debug': debug_info_str
    }


def check_anomaly(obj, current_tag_stats, anomaly_type):
    if not current_tag_stats:
        return None

    object_tag = obj.get('objectTag')

    if anomaly_type == 'Mass':
        scan_key = 'log_mass'
        current_value = math.log10(obj.get('mass', 1.0) + 1e-6)
        mode_key = 'mass_mode'
        bug_type = "Physics Mass Anomaly"
        
        is_bug = current_value > current_tag_stats['log_mass_high'] or \
                     current_value < current_tag_stats['log_mass_low']
        fix_prefix = "SET_MASS"
        value_display = f"Mass ({obj.get('mass', 1.0):.2f})"
        
    elif anomaly_type == 'Drag':
        scan_key = 'drag'
        current_value = obj.get('drag', 0.0)
        mode_key = 'drag_mode'
        bug_type = "Physics Drag Anomaly"
        
        is_bug = current_value > current_tag_stats['drag_high']
        fix_prefix = "SET_DRAG"
        value_display = f"Linear Drag ({current_value:.2f})"


    elif anomaly_type == 'AngularDrag':
        scan_key = 'angular_drag'
        current_value = obj.get('angularDrag', 0.05)
        mode_key = 'angular_drag_mode'
        bug_type = "Physics Angular Drag Anomaly"
        
        is_bug = current_value > current_tag_stats['angular_drag_high']
        fix_prefix = "SET_ANGULAR_DRAG"
        value_display = f"Angular Drag ({current_value:.2f})"

    else:
        return None

    if is_bug:
        mode_value = current_tag_stats.get(mode_key, 1.0)
        
        suggestion = f"{value_display} is significantly {'high' if anomaly_type != 'Mass' else 'outlier'} compared to objects with tag '{object_tag}'. The majority value suggests a typical value of ~{mode_value:.2f}."
        fix_action = f"{fix_prefix}:{mode_value:.2f}"
        
        debug_info_str = f"Current Value: {current_value:.2f}, High: {current_tag_stats.get(f'{scan_key}_high', 'N/A'):.2f}, Low: {current_tag_stats.get(f'{scan_key}_low', 'N/A')}"
        
        return {
            'bug_type': bug_type, 
            'suggestion': suggestion, 
            'fix_action': fix_action,
            'debug': debug_info_str
        }
    return None


def get_history_file_path(project_name):
    history_file_name = f"{project_name}_bug_history.json"
    return os.path.join(SCRIPT_DIR, history_file_name)

def load_bug_history(project_name):
    history_file = get_history_file_path(project_name)
    tag_score_history = {} 
    
    if os.path.exists(history_file):
        try:
            with open(history_file, 'r') as f:
                history_list = json.load(f)
                for item in history_list:
                    tag_score_history[(item['object_tag'], item['bug_type'])] = item.get('confidence_score', BASE_CONFIDENCE)
                return tag_score_history
        except json.JSONDecodeError:
            print(f"Error loading {history_file}. Starting fresh.")
            return {}
    return {}

def save_bug_history(project_name, tag_score_history):
    history_file_path = get_history_file_path(project_name)
    
    history_list = []
    for (tag, bug_type), confidence_score in tag_score_history.items():
        history_list.append({
            'object_tag': tag,
            'bug_type': bug_type,
            'confidence_score': confidence_score
        })
    
    try:
        with open(history_file_path, 'w') as f:
            json.dump(history_list, f, indent=4)
    except IOError as e:
        print(f"Error saving history to {history_file_path}: {e}")

def load_tag_collider_map():
    if not os.path.exists(TAG_COLLIDER_MAP_PATH):
        status_message = f"Warning: Tag Collider Map file not found at '{TAG_COLLIDER_MAP_PATH}'. Mismatch Collider rule is disabled."
        return {}, status_message
        
    try:
        with open(TAG_COLLIDER_MAP_PATH, 'r') as f:
            data = json.load(f)
            
            tag_map = {}
            for entry in data.get('entries', []):
                
                collider_type_from_json = entry.get('colliderType')
                if collider_type_from_json is None:
                    collider_type_string = "None"
                else:
                    collider_type_string = str(collider_type_from_json)

                tag_map[entry['tag']] = {
                    'colliderType': collider_type_string,
                    'isStatic': entry.get('isStatic', False)
                }
            
            status_message = f"Success: Loaded {len(tag_map)} entries from Tag Collider Map."
            return tag_map, status_message
            
    except (json.JSONDecodeError, KeyError, IOError) as e:
        status_message = f"Error loading {TAG_COLLIDER_MAP_PATH}: {e}. Mismatch Collider rule is disabled due to file error."
        return {}, status_message


@app.route('/analyze-physics', methods=['POST'])
def analyze_physics_data():
    try:
        data = request.json['objects']
        project_name = request.json.get('projectName', 'DefaultProject') 
        selected_tags = request.json.get('selectedTags', []) 
        scan_settings_global = request.json.get('scanSettings', {})
    except (TypeError, KeyError):
        return jsonify({"error": "Invalid input format"}), 400

    current_bugs = []
    tag_collider_map, map_load_status = load_tag_collider_map()
    
    if selected_tags and 'Everything' not in selected_tags: 
        scannable_objects = [obj for obj in data if obj.get('objectTag') in selected_tags]
    else:
        scannable_objects = data 

    tag_stats = {}
    physics_objects_by_tag = {}

    for obj in scannable_objects:
        if has_rigidbody(obj):
            tag = obj.get('objectTag')
            if tag not in physics_objects_by_tag:
                physics_objects_by_tag[tag] = []
            physics_objects_by_tag[tag].append(obj)

    std_multiplier = CONFIG['physics_thresholds'].get('anomaly_std_dev_multiplier', 2)

    for tag, objects in physics_objects_by_tag.items():
        all_log_masses = [math.log10(obj.get('mass', 1.0) + 1e-6) for obj in objects]
        all_drag = [obj.get('drag', 0.0) for obj in objects]
        all_angular_drag = [obj.get('angularDrag', 0.05) for obj in objects] 
        all_mass = [obj.get('mass', 1.0) for obj in objects] 
        all_drag_raw = [obj.get('drag', 0.0) for obj in objects]
        all_angular_drag_raw = [obj.get('angularDrag', 0.05) for obj in objects] 

        log_mass_median = calculate_median(all_log_masses)
        log_mass_mad = calculate_mad(all_log_masses, log_mass_median)
        if log_mass_mad < 1e-6:
             _, log_mass_std_robust = calculate_stats(all_log_masses)
        else:
            log_mass_std_robust = log_mass_mad * MAD_SCALE_FACTOR
        log_mass_high = log_mass_median + std_multiplier * log_mass_std_robust
        log_mass_low = log_mass_median - std_multiplier * log_mass_std_robust

        drag_median = calculate_median(all_drag)
        drag_mad = calculate_mad(all_drag, drag_median)
        if drag_mad < 1e-6:
            _, drag_std_robust = calculate_stats(all_drag)
        else:
            drag_std_robust = drag_mad * MAD_SCALE_FACTOR
        drag_high = drag_median + std_multiplier * drag_std_robust

        angular_drag_median = calculate_median(all_angular_drag)
        angular_drag_mad = calculate_mad(all_angular_drag, angular_drag_median)
        if angular_drag_mad < 1e-6:
            _, angular_drag_std_robust = calculate_stats(all_angular_drag)
        else:
            angular_drag_std_robust = angular_drag_mad * MAD_SCALE_FACTOR
        angular_drag_high = angular_drag_median + std_multiplier * angular_drag_std_robust

        mass_mode = calculate_mode(all_mass)
        drag_mode = calculate_mode(all_drag_raw)
        angular_drag_mode = calculate_mode(all_angular_drag_raw)

        tag_stats[tag] = {
            'log_mass_median': log_mass_median, 'log_mass_std_robust': log_mass_std_robust,
            'log_mass_high': log_mass_high, 'log_mass_low': log_mass_low,
            'drag_median': drag_median, 'drag_std_robust': drag_std_robust, 'drag_high': drag_high,
            'angular_drag_median': angular_drag_median, 'angular_drag_std_robust': angular_drag_std_robust,   
            'angular_drag_high': angular_drag_high, 
            'mass_mode': mass_mode, 'drag_mode': drag_mode, 'angular_drag_mode': angular_drag_mode
        }
    
    tag_score_history = load_bug_history(project_name)

    def get_confidence_score(bug_type, object_tag):
        return tag_score_history.get((object_tag, bug_type), BASE_CONFIDENCE)
    
    current_bug_tags = set()
    alert_for_ignore = None
    alert_threshold = IGNORE_THRESHOLD + 0.1 

    def add_bug_if_needed(bug_type, suggestion, fix_action, debug_info=""):
        nonlocal alert_for_ignore 
        confidence = get_confidence_score(bug_type, object_tag)
        
        if not alert_for_ignore and math.isclose(confidence, alert_threshold):
              alert_for_ignore = {
                "message": f"Continuing to ignore the '{bug_type}' bug for the '{object_tag}' tag will cause it to be hidden permanently for all objects with this tag.",
                "bug_type": bug_type,
                "object_tag": object_tag
            }

        if confidence > IGNORE_THRESHOLD:
            current_bugs.append({
                "object_name": object_name, 
                "object_tag": object_tag, 
                "object_path": object_path,
                "bug_type": bug_type,
                "suggestion": suggestion,
                "confidence_score": confidence,
                "fix_action": fix_action 
            })
            current_bug_tags.add((object_tag, bug_type))

        if confidence > IGNORE_THRESHOLD:
            print(f"\n--- {bug_type} Debug for {object_name} (Tag: {object_tag}) ---")
            
            if debug_info: 
                print(debug_info)
            
            print(f"RESULT: BUG REPORTED: {bug_type} (Fix: {fix_action})")
            print("----------------------------------------------------------------")
            sys.stdout.flush()

    for obj in scannable_objects:
        scan_settings = obj.get('scanSettings', scan_settings_global)
        obj_has_rb = has_rigidbody(obj) 
        obj_has_collider = obj.get('colliderType') not in ('', 'None')
        object_tag = obj.get('objectTag')
        object_name = obj.get('objectName')
        object_path = obj.get('objectPath')
        
        tag_info = tag_collider_map.get(object_tag, {})
        tag_is_static = tag_info.get('isStatic', True)

        current_tag_stats = tag_stats.get(object_tag)
        mismatch_bug_added = False

        if scan_settings.get('scanForMissingRigidbody', True):
            bug_details = check_missing_rigidbody(obj, tag_is_static, obj_has_rb, tag_info)
            if bug_details:
                add_bug_if_needed(bug_details['bug_type'], bug_details['suggestion'], bug_details['fix_action'], debug_info=bug_details.get('debug', ""))
        
        if scan_settings.get('scanForMismatchCollider', True):
            bug_details = check_mismatch_collider(obj, tag_collider_map)
            if bug_details:
                add_bug_if_needed(bug_details['bug_type'], bug_details['suggestion'], bug_details['fix_action'], debug_info=bug_details.get('debug', ""))
                mismatch_bug_added = True

        if scan_settings.get('scanForMeshConvexCollider', True) and not mismatch_bug_added:
            bug_details = check_mesh_convex_collider_bug(obj, obj_has_rb)
            if bug_details:
                add_bug_if_needed(bug_details['bug_type'], bug_details['suggestion'], bug_details['fix_action'])
                            
        if scan_settings.get('scanForKinematicGravity', True):
            bug_details = check_kinematic_gravity(obj, obj_has_rb)
            if bug_details:
                add_bug_if_needed(bug_details['bug_type'], bug_details['suggestion'], bug_details['fix_action'])
        
        if scan_settings.get('scanForMissingCollider', True):
            bug_details = check_missing_collider(obj, obj_has_collider, tag_collider_map, tag_info)
            if bug_details:
                add_bug_if_needed(bug_details['bug_type'], bug_details['suggestion'], bug_details['fix_action'], debug_info=bug_details.get('debug', ""))

        if obj_has_rb and current_tag_stats:
            
            if scan_settings.get('scanForMassAnomaly', True):
                bug_details = check_anomaly(obj, current_tag_stats, 'Mass')
                if bug_details:
                    add_bug_if_needed(bug_details['bug_type'], bug_details['suggestion'], bug_details['fix_action'], debug_info=bug_details.get('debug'))

            if scan_settings.get('scanForDragAnomaly', True):
                bug_details = check_anomaly(obj, current_tag_stats, 'Drag')
                if bug_details:
                    add_bug_if_needed(bug_details['bug_type'], bug_details['suggestion'], bug_details['fix_action'], debug_info=bug_details.get('debug'))
                                    
            if scan_settings.get('scanForAngularDragAnomaly', True):
                bug_details = check_anomaly(obj, current_tag_stats, 'AngularDrag')
                if bug_details:
                    add_bug_if_needed(bug_details['bug_type'], bug_details['suggestion'], bug_details['fix_action'], debug_info=bug_details.get('debug'))
        
        

    updated_tag_scores = tag_score_history.copy()
    fixed_bug_references = []

    for (tag, bug_type), previous_score in tag_score_history.items():
        is_current = (tag, bug_type) in current_bug_tags
        
        if not is_current:
            if previous_score <= IGNORE_THRESHOLD:
                continue
                
            if previous_score < BASE_CONFIDENCE: 
                new_score = min(1.0, previous_score + 0.1)
                updated_tag_scores[(tag, bug_type)] = new_score
                
                sys.stdout.flush()
                
                fixed_bug_references.append({
                    "object_tag": tag, 
                    "bug_type": bug_type
                })
        
        if updated_tag_scores.get((tag, bug_type), 0) <= IGNORE_THRESHOLD:
            updated_tag_scores.pop((tag, bug_type), None)

    for tag_type_pair in current_bug_tags:
        if tag_type_pair not in updated_tag_scores:
            updated_tag_scores[tag_type_pair] = BASE_CONFIDENCE
            
    save_bug_history(project_name, updated_tag_scores) 

    def clean_bug(bug):
        return {k: v for k, v in bug.items() if k not in ['confidence_score']} 
    
    response = {
        "current_bugs": [clean_bug(b) for b in current_bugs], 
        "fixed_bug_references": fixed_bug_references, 
        "map_load_status": map_load_status
    }
    
    if alert_for_ignore:
        response['alert_dialog'] = alert_for_ignore

    return jsonify(response)

@app.route('/ignore-bug', methods=['POST'])
def ignore_bug():
    data = request.json
    bug_type = data.get('bugType')
    object_tag = data.get('objectTag')
    project_name = data.get('projectName', 'DefaultProject')
    
    if not bug_type or not object_tag:
        return jsonify({"error": "Missing bug details"}), 400

    tag_score_history = load_bug_history(project_name) 
    bug_key = (object_tag, bug_type)
    
    current_score = tag_score_history.get(bug_key, BASE_CONFIDENCE)
    
    new_score = max(0.0, current_score - 0.1)
    
    tag_score_history[bug_key] = new_score
    
    print(f"[RL Update] Bug IGNORED: Tag '{object_tag}', Type '{bug_type}'. Confidence: {current_score:.2f} -> {new_score:.2f}")
    sys.stdout.flush()
    
    save_bug_history(project_name, tag_score_history)
    
    return jsonify({"status": f"Confidence for {bug_type} under tag {object_tag} reduced from {current_score:.2f} to {new_score:.2f} for {project_name}."}), 200

@app.route('/report-fix', methods=['POST'])
def report_fix():
    data = request.json
    bug_type = data.get('bugType')
    object_tag = data.get('objectTag')
    project_name = data.get('projectName', 'DefaultProject')
    object_path = data.get('objectPath')
    
    if not bug_type or not object_tag:
        return jsonify({"error": "Missing bug details"}), 400

    tag_score_history = load_bug_history(project_name) 
    bug_key = (object_tag, bug_type)
    
    current_score = tag_score_history.get(bug_key, BASE_CONFIDENCE)
    
    new_score = min(1.0, current_score + 0.1)
    
    tag_score_history[bug_key] = new_score
    print(f"[RL Update] Bug EXPLICITLY FIXED: Path '{object_path}', Tag '{object_tag}', Type '{bug_type}'. Confidence: {current_score:.2f} -> {new_score:.2f}")
    sys.stdout.flush()
    
    save_bug_history(project_name, tag_score_history)
    
    return jsonify({"status": f"Confidence for {bug_type} under tag {object_tag} increased from {current_score:.2f} to {new_score:.2f} for {project_name}."}), 200

if __name__ == '__main__':
    app.run(port=5000, debug=True)