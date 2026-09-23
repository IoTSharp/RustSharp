struct Marker;

impl Drop for Marker {
    fn drop(&mut self) {
        println!("drop");
    }
}

fn emit() {
    let _marker = Marker;
    println!("body");
}

fn main() {
    emit();
}
