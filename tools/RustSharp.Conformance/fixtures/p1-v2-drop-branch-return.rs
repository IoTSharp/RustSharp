struct First; struct Second;
impl Drop for First { fn drop(&mut self) { println!("first"); } }
impl Drop for Second { fn drop(&mut self) { println!("second"); } }
fn emit(early: bool) { let _first = First; if early { let _second = Second; println!("early"); return; } println!("late"); }
fn main() { emit(true); emit(false); }
