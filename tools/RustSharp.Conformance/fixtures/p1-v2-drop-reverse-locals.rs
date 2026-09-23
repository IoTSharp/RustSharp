struct First; struct Second;
impl Drop for First { fn drop(&mut self) { println!("first"); } }
impl Drop for Second { fn drop(&mut self) { println!("second"); } }
fn main() { let _first = First; let _second = Second; println!("body"); }
