struct First;
struct Second;

impl Drop for First {
    fn drop(&mut self) {
        println!("first");
    }
}

impl Drop for Second {
    fn drop(&mut self) {
        println!("second");
    }
}

fn emit(flag: bool) {
    let _first = First;
    if flag {
        let _second = Second;
        println!("early");
        return;
    }
    println!("late");
}

fn main() {
    emit(true);
    emit(false);
}
